// Copyright 2017 Google Inc. All Rights Reserved.
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

using System.Data.Common;
using Google.Cloud.EntityFrameworkCore.Spanner.Diagnostics;
using Google.Cloud.Spanner.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Google.Cloud.EntityFrameworkCore.Spanner.Storage.Internal
{
    /// <summary>
    /// </summary>
    public class SpannerRelationalConnection : RelationalConnection
    {
        /// <summary>
        /// </summary>
        /// <param name="dependencies"></param>
        public SpannerRelationalConnection(RelationalConnectionDependencies dependencies)
            : base(dependencies)
        {
        }

        /// <inheritdoc />
        public override bool IsMultipleActiveResultSetsEnabled => true;

        /// <inheritdoc />
        protected override DbConnection CreateDbConnection()
            => new SpannerConnection(ConnectionString)
            {
                //This will route all contextual logs through the EF logger to give a consistent logging experience.
                Logger = new SpannerLogBridge<DbLoggerCategory.Database.Connection>(Dependencies.ConnectionLogger)
            };
    }
}