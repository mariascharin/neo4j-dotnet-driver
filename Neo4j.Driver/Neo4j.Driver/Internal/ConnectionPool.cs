﻿// Copyright (c) 2002-2017 "Neo Technology,"
// Network Engine for Objects in Lund AB [http://neotechnology.com]
// 
// This file is part of Neo4j.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
using System;
using System.Collections.Generic;
using Neo4j.Driver.Internal.Connector;
using Neo4j.Driver.V1;

namespace Neo4j.Driver.Internal
{
    internal class ConnectionPool : LoggerBase, IConnectionPool
    {
        private readonly int _idleSessionPoolSize;
        private readonly ConnectionSettings _connectionSettings;

        private readonly ILogger _logger;

        private readonly Queue<IPooledConnection> _availableConnections = new Queue<IPooledConnection>();
        private readonly Dictionary<Guid, IPooledConnection> _inUseConnections = new Dictionary<Guid, IPooledConnection>();

        private readonly IConnectionErrorHandler _externalErrorHandler;

        private volatile bool _disposeCalled;

        // for test only
        private readonly IConnection _fakeConnection;
        internal int NumberOfInUseConnections => _inUseConnections.Count;
        internal int NumberOfAvailableConnections => _availableConnections.Count;

        internal bool DisposeCalled
        {
            set { _disposeCalled = value; }
        }

        public ConnectionPool(
            ConnectionSettings connectionSettings,
            ConnectionPoolSettings connectionPoolSettings,
            ILogger logger,
            IConnectionErrorHandler exteralErrorHandler = null)
            : base(logger)
        {
            _connectionSettings = connectionSettings;
            _idleSessionPoolSize = connectionPoolSettings.MaxIdleSessionPoolSize;

            _externalErrorHandler = exteralErrorHandler;
            _logger = logger;
        }

        internal ConnectionPool(
            IConnection connection,
            Queue<IPooledConnection> availableConnections = null,
            Dictionary<Guid, IPooledConnection> inUseConnections = null,
            ILogger logger = null,
            ConnectionPoolSettings settings = null,
            IConnectionErrorHandler exteralErrorHandler = null)
            : this(null, settings ?? new ConnectionPoolSettings(Config.DefaultConfig.MaxIdleSessionPoolSize), 
                  logger, exteralErrorHandler)
        {
            _fakeConnection = connection;
            _availableConnections = availableConnections ?? new Queue<IPooledConnection>();
            _inUseConnections = inUseConnections ?? new Dictionary<Guid, IPooledConnection>();
        }

        private IPooledConnection CreateNewPooledConnection()
        {
            PooledConnection conn = null;
            try
            {
                conn = _fakeConnection != null
                    ? new PooledConnection(_fakeConnection, Release)
                    : new PooledConnection(new SocketConnection(_connectionSettings, _logger), Release);
                if (_externalErrorHandler != null)
                {
                    conn.AddConnectionErrorHander(_externalErrorHandler);
                }
                conn.Init();
                return conn;
            }
            catch
            {
                // shut down and clean all the resources of the conneciton if failed to establish
                conn?.Close();
                throw;
            }

        }

        private void ThrowConnectionPoolClosedException()
        {
            throw new ObjectDisposedException(GetType().Name, "Cannot acquire a new connection from the connection pool as the pool has already been disposed.");
        }

        public IPooledConnection Acquire()
        {
            return TryExecute(() =>
            {
                if (_disposeCalled)
                {
                    ThrowConnectionPoolClosedException();
                }
                IPooledConnection connection = null;
                lock (_availableConnections)
                {
                    if (_availableConnections.Count != 0)
                        connection = _availableConnections.Dequeue();
                }

                if (connection == null)
                {
                    connection = CreateNewPooledConnection();
                }
                else if (!connection.IsOpen)
                {
                    connection.Close();
                    return Acquire();
                }

                lock (_inUseConnections)
                {
                    if (_disposeCalled)
                    {
                        connection.Close();
                        ThrowConnectionPoolClosedException();
                    }
                    _inUseConnections.Add(connection.Id, connection);
                }
                return connection;
            });
        }

        private bool IsConnectionReusable(IPooledConnection connection)
        {
            if (!connection.IsOpen)
            {
                return false;
            }

            try
            {
                connection.ClearConnection();
            }
            catch
            {
                return false;
            }
            return true;
        }

        private bool IsPoolFull()
        {
            return _availableConnections.Count >= _idleSessionPoolSize && _idleSessionPoolSize != Config.InfiniteMaxIdleSessionPoolSize;
        }

        public void Release(Guid id)
        {
            TryExecute(() =>
            {
                if (_disposeCalled)
                {
                    // pool already disposed
                    return;
                }
                IPooledConnection connection;
                lock (_inUseConnections)
                {
                    if (!_inUseConnections.ContainsKey(id))
                    {
                        // pool already disposed
                        return;
                    }

                    connection = _inUseConnections[id];
                    _inUseConnections.Remove(id);
                }

                if (IsConnectionReusable(connection))
                {
                    lock (_availableConnections)
                    {
                        if (_disposeCalled || IsPoolFull())
                        {
                            connection.Close();
                        }
                        else
                        {
                            _availableConnections.Enqueue(connection);
                        }
                    }
                }
                else
                {
                    //release resources by connection
                    connection.Close();
                }
            });
        }

        // For concurrent calling: you are free to get something from inUseConn or availConn when we dispose.
        // However it is forbiden to put something back to the conn queues after we've already started disposing.
        protected override void Dispose(bool isDisposing)
        {
            if (!isDisposing)
            {
                return;
            }

            TryExecute(() =>
            {
                _disposeCalled = true;
                lock (_inUseConnections)
                {
                    var connections = new List<IPooledConnection>(_inUseConnections.Values);
                    _inUseConnections.Clear();
                    foreach (var inUseConnection in connections)
                    {
                        Logger?.Info($"Disposing In Use Connection {inUseConnection.Id}");
                        inUseConnection.Close();
                    }
                }
                lock (_availableConnections)
                {
                    while (_availableConnections.Count > 0)
                    {
                        var connection = _availableConnections.Dequeue();
                        Logger?.Debug($"Disposing Available Connection {connection.Id}");
                        connection.Close();
                    }
                }
            });
            base.Dispose(true);
        }
    }

    internal interface IConnectionPool : IDisposable
    {
        IPooledConnection Acquire();
        void Release(Guid id);
    }

    internal interface IPooledConnection : IConnection
    {
        /// <summary>
        /// An identifer of this connection for pooling
        /// </summary>
        Guid Id { get; }

        /// <summary>
        /// Try to reset the connection to a clean state to prepare it for a new session.
        /// </summary>
        void ClearConnection();

        /// <summary>
        /// Return true if unrecoverable error has been received on this connection, otherwise false.
        /// </summary>
        bool HasUnrecoverableError { get; }
    }
}