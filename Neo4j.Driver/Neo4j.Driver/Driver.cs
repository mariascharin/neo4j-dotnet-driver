﻿//  Copyright (c) 2002-2016 "Neo Technology,"
//  Network Engine for Objects in Lund AB [http://neotechnology.com]
// 
//  This file is part of Neo4j.
// 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
// 
//      http://www.apache.org/licenses/LICENSE-2.0
// 
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
using System;

namespace Neo4j.Driver
{
    public class Driver : LoggerBase, IDisposable
    {
        private readonly Config _config;
        private readonly Uri _url;
        public Uri Uri => _url;

        internal Driver(Uri url, Config config) : base(config?.Logger)
        {
            if (url.Scheme.ToLowerInvariant() == "bolt" && url.Port == -1)
            {
                var builder = new UriBuilder(url.Scheme, url.Host, 7687);
                url = builder.Uri;
            }

            _url = url;
            _config = config;
        }

        protected override void Dispose(bool isDisposing)
        {
           
            if (!isDisposing)
                return;
            Logger?.Dispose();
        }

        /// <summary>
        ///     Establish a session with Neo4j instance
        /// </summary>
        /// <returns>
        ///     An <see cref="ISession" /> that could be used to <see cref="ISession.Run" /> a statement or begin a
        ///     transaction
        /// </returns>
        public ISession Session()
        {
            return new Session(_url, _config);
        }
    }
}