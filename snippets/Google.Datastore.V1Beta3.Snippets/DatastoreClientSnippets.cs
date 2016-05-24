﻿// Copyright 2016 Google Inc. All Rights Reserved.
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
using Google.Protobuf;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using static Google.Datastore.V1Beta3.CommitRequest.Types;
using static Google.Datastore.V1Beta3.PropertyFilter.Types;
using static Google.Datastore.V1Beta3.PropertyOrder.Types;
using static Google.Datastore.V1Beta3.ReadOptions.Types;

namespace Google.Datastore.V1Beta3.Snippets
{
    [Collection(nameof(DatastoreSnippetFixture))]
    public class DatastoreClientSnippets
    {
        private readonly DatastoreSnippetFixture _fixture;

        public DatastoreClientSnippets(DatastoreSnippetFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public void Lookup()
        {
            string projectId = _fixture.ProjectId;
            string namespaceId = _fixture.NamespaceId;

            // Snippet: Lookup
            KeyFactory keyFactory = new KeyFactory(projectId, namespaceId, "book");
            Key key1 = keyFactory.CreateKey("pride_and_prejudice");
            Key key2 = keyFactory.CreateKey("not_present");

            DatastoreClient client = DatastoreClient.Create();
            LookupResponse response = client.Lookup(
                projectId,
                new ReadOptions { ReadConsistency = ReadConsistency.Strong },
                new[] { key1, key2 });
            Console.WriteLine($"Found: {response.Found.Count}");
            Console.WriteLine($"Deferred: {response.Deferred.Count}");
            Console.WriteLine($"Missing: {response.Missing.Count}");
            // End snippet

            Entity entity = response.Found[0].Entity;
            Assert.Equal("Jane Austen", (string)entity["author"]);
            Assert.Equal("Pride and Prejudice", (string)entity["title"]);
        }

        [Fact]
        public void StructuredQuery()
        {
            string projectId = _fixture.ProjectId;
            PartitionId partitionId = _fixture.PartitionId;
            
            // Snippet: RunQuery(string,PartitionId,ReadOptions,Query,CallSettings)
            DatastoreClient client = DatastoreClient.Create();
            Query query = new Query
            {
                Kind = { "book" },
                Filter = new Filter
                {
                    PropertyFilter = new PropertyFilter
                    {
                        Property = new PropertyReference("author"),
                        Op = Operator.Equal,
                        Value = "Jane Austen"
                    }
                }
            };
            RunQueryResponse response = client.RunQuery(
                projectId,
                partitionId,
                new ReadOptions { ReadConsistency = ReadConsistency.Eventual },
                query);

            foreach (EntityResult result in response.Batch.EntityResults)
            {
                Console.WriteLine(result.Entity);
            }
            // EndSnippet

            Assert.Equal(1, response.Batch.EntityResults.Count);
            Entity entity = response.Batch.EntityResults[0].Entity;
            Assert.Equal("Jane Austen", (string)entity["author"]);
            Assert.Equal("Pride and Prejudice", (string)entity["title"]);
        }

        [Fact]
        public void GqlQuery()
        {
            string projectId = _fixture.ProjectId;
            PartitionId partitionId = _fixture.PartitionId;

            // Snippet: RunQuery(string,PartitionId,ReadOptions,GqlQuery,CallSettings)
            DatastoreClient client = DatastoreClient.Create();
            GqlQuery gqlQuery = new GqlQuery
            {
                QueryString = "SELECT * FROM book WHERE author = @author",
                NamedBindings = { { "author", new GqlQueryParameter { Value = "Jane Austen" } } },
            };
            RunQueryResponse response = client.RunQuery(
                projectId,
                partitionId,
                new ReadOptions { ReadConsistency = ReadConsistency.Eventual },
                gqlQuery);

            foreach (EntityResult result in response.Batch.EntityResults)
            {
                Console.WriteLine(result.Entity);
            }
            // End snippet

            Assert.Equal(1, response.Batch.EntityResults.Count);
            Entity entity = response.Batch.EntityResults[0].Entity;
            Assert.Equal("Jane Austen", (string)entity["author"]);
            Assert.Equal("Pride and Prejudice", (string)entity["title"]);
        }

        [Fact]
        public void AddEntity()
        {
            string projectId = _fixture.ProjectId;
            string namespaceId = _fixture.NamespaceId;

            // TODO: Fix transaction handling. (Should roll back automatically.)

            // Snippet: Commit(string,Mode,ByteString,*,CallSettings)
            DatastoreClient client = DatastoreClient.Create();
            KeyFactory keyFactory = new KeyFactory(projectId, namespaceId, "book");
            Entity book1 = new Entity
            {
                Key = keyFactory.CreateInsertionKey(),
                ["author"] = "Harper Lee",
                ["title"] = "To Kill a Mockingbird",
                ["publication_date"] = new DateTime(1960, 7, 11, 0, 0, 0, DateTimeKind.Utc)
            };
            Entity book2 = new Entity
            {
                Key = keyFactory.CreateInsertionKey(),
                ["author"] = "Charlotte Brontë",
                ["title"] = "Jane Eyre",
                ["publication_date"] = new DateTime(1847, 10, 16, 0, 0, 0, DateTimeKind.Utc)
            };
            ByteString transaction = client.BeginTransaction(projectId).Transaction;
            CommitResponse response = client.Commit(
                projectId,
                Mode.Transactional,
                transaction,
                new[] { book1.ToInsert(), book2.ToInsert() });

            IEnumerable<Key> insertedKeys = response.MutationResults.Select(r => r.Key);
            Console.WriteLine($"Inserted keys: {string.Join(",", insertedKeys)}");
            // End snippet
        }

        [Fact]
        public void AllocateIds()
        {
            string projectId = _fixture.ProjectId;
            string namespaceId = _fixture.NamespaceId;

            // Snippet: AllocateIds
            DatastoreClient client = DatastoreClient.Create();
            KeyFactory keyFactory = new KeyFactory(projectId, namespaceId, "message");
            AllocateIdsResponse response = client.AllocateIds(projectId,
                new[] { keyFactory.CreateInsertionKey(), keyFactory.CreateInsertionKey() }
            );
            Entity entity1 = new Entity { Key = response.Keys[0], ["text"] = "Text 1" };
            Entity entity2 = new Entity { Key = response.Keys[1], ["text"] = "Text 2" };
            // End snippet

            Assert.NotEqual(entity1, entity2);
        }

        [Fact]
        public void NamespaceQuery()
        {
            string projectId = _fixture.ProjectId;

            // Snippet: NamespaceQuery
            DatastoreClient client = DatastoreClient.Create();
            PartitionId partitionId = new PartitionId(projectId);
            RunQueryResponse response = client.RunQuery(projectId, partitionId, null,
                new Query { Kind = { DatastoreConstants.NamespaceKind } });
            foreach (EntityResult result in response.Batch.EntityResults)
            {
                Console.WriteLine(result.Entity.Key.Path.Last().Name);
            }
            // End snippet
        }

        [Fact]
        public void KindQuery()
        {
            string projectId = _fixture.ProjectId;
            string namespaceId = _fixture.NamespaceId;
            PartitionId partitionId = new PartitionId(projectId, namespaceId);

            // Snippet: KindQuery
            DatastoreClient client = DatastoreClient.Create();
            RunQueryResponse response = client.RunQuery(projectId, partitionId, null,
                new Query { Kind = { DatastoreConstants.KindKind } });
            foreach (EntityResult result in response.Batch.EntityResults)
            {
                Console.WriteLine(result.Entity.Key.Path.Last().Name);
            }
            // End snippet
        }

        [Fact]
        public void PropertyQuery()
        {
            string projectId = _fixture.ProjectId;
            string namespaceId = _fixture.NamespaceId;
            PartitionId partitionId = new PartitionId(projectId, namespaceId);

            // Snippet: PropertyQuery
            DatastoreClient client = DatastoreClient.Create();
            RunQueryResponse response = client.RunQuery(projectId, partitionId, null,
                new Query
                {
                    Kind = { DatastoreConstants.PropertyKind },
                    Projection = { DatastoreConstants.KeyProperty }
                });
            foreach (EntityResult result in response.Batch.EntityResults)
            {
                Key key = result.Entity.Key;
                string propertyName = key.Path.Last().Name;
                string kind = key.GetParent().Path.Last().Name;
                Console.WriteLine($"Kind: {kind}; Property: {propertyName}");
            }
            // End snippet
        }

        [Fact]
        public void Overview()
        {
            string projectId = _fixture.ProjectId;
            string namespaceId = _fixture.NamespaceId;
            // Snippet: Overview
            var client = DatastoreClient.Create();

            var keyFactory = new KeyFactory(projectId, namespaceId, "message");
            var entity = new Entity
            {
                Key = keyFactory.CreateInsertionKey(),
                ["created"] = DateTime.UtcNow,
                ["text"] = "Text of the message"
            };
            var transaction = client.BeginTransaction(projectId).Transaction;
            var commitResponse = client.Commit(projectId, Mode.Transactional, transaction, new[] { entity.ToInsert() });
            var insertedKey = commitResponse.MutationResults[0].Key;
            // End snippet
        }

        // Snippets ported from https://cloud.google.com/datastore/docs/concepts/entities

        [Fact]
        public void CreateEntity()
        {
            string projectId = _fixture.ProjectId;
            string namespaceId = _fixture.NamespaceId;

            // Snippet: CreateEntity
            KeyFactory keyFactory = new KeyFactory(projectId, namespaceId, "Task");
            Entity entity = new Entity
            {
                Key = keyFactory.CreateInsertionKey(),
                ["type"] = "Personal",
                ["done"] = false,
                ["priority"] = 4,
                ["description"] = "Learn Cloud Datastore",
                ["percent_complete"] = 75.0
            };
            // End snippet
        }

        [Fact]
        public void InsertEntity()
        {
            string projectId = _fixture.ProjectId;
            string namespaceId = _fixture.NamespaceId;

            // Snippet: InsertEntity
            KeyFactory keyFactory = new KeyFactory(projectId, namespaceId, "Task");
            Entity entity = new Entity
            {
                Key = keyFactory.CreateInsertionKey(),
                ["type"] = "Personal",
                ["done"] = false,
                ["priority"] = 4,
                ["description"] = "Learn Cloud Datastore",
                ["percent_complete"] = 75.0
            };
            DatastoreClient client = DatastoreClient.Create();
            CommitResponse response = client.Commit(projectId, Mode.NonTransactional, new[] { entity.ToInsert() });
            Key insertedKey = response.MutationResults[0].Key;
            // End snippet
        }

        [Fact]
        public void LookupEntity()
        {
            string projectId = _fixture.ProjectId;
            Key key = _fixture.LearnDatastoreKey;

            // Snippet: LookupEntity
            DatastoreClient client = DatastoreClient.Create();
            LookupResponse response = client.Lookup(
                projectId,
                new ReadOptions { ReadConsistency = ReadConsistency.Eventual },
                new[] { key });

            Entity entity = response.Found[0].Entity;
            // End snippet
        }

        [Fact]
        public void UpdateEntity()
        {
            string projectId = _fixture.ProjectId;
            Key key = _fixture.LearnDatastoreKey;

            // TODO: Fix transaction handling
            // Snippet: UpdateEntity
            DatastoreClient client = DatastoreClient.Create();
            ByteString transaction = client.BeginTransaction(projectId).Transaction;

            LookupResponse response = client.Lookup(
                projectId,
                new ReadOptions { ReadConsistency = ReadConsistency.Strong, Transaction = transaction },
                new[] { key });

            Entity entity = response.Found[0].Entity;
            entity["priority"] = 5;
            client.Commit(projectId, Mode.Transactional, transaction, new[] { entity.ToUpdate() });
            // End snippet
        }

        [Fact]
        public void DeleteEntity()
        {
            string projectId = _fixture.ProjectId;
            string namespaceId = _fixture.NamespaceId;

            // Copied from InsertEntity; we want to create a new one to delete.
            KeyFactory keyFactory = new KeyFactory(projectId, namespaceId, "Task");
            Entity entity = new Entity
            {
                Key = keyFactory.CreateInsertionKey(),
                ["type"] = "Personal",
                ["done"] = false,
                ["priority"] = 4,
                ["description"] = "Learn Cloud Datastore",
                ["percent_complete"] = 75.0
            };
            DatastoreClient insertClient = DatastoreClient.Create();
            CommitResponse response = insertClient.Commit(projectId, Mode.NonTransactional, new[] { entity.ToInsert() });
            Key key = response.MutationResults[0].Key;

            // Snippet: DeleteEntity
            DatastoreClient client = DatastoreClient.Create();
            // If you have an entity instead of just a key, then entity.ToDelete() would work too.
            CommitResponse commit = insertClient.Commit(projectId, Mode.NonTransactional, new[] { key.ToDelete() });
            // End snippet
        }

        // Batch lookup etc are currently obvious given the array creation.
        // If we simplify single-entity operations, we may need more snippets here.

        [Fact]
        public void AncestorPaths()
        {
            string projectId = _fixture.ProjectId;
            string namespaceId = _fixture.NamespaceId;

            // Snippet: AncestorPaths
            KeyFactory keyFactory = new KeyFactory(projectId, namespaceId, "User");
            Key taskKey = keyFactory.CreateKey("alice").WithElement("Task", "sampleTask");

            Key multiLevelKey = keyFactory
                .CreateKey("alice")
                .WithElement("TaskList", "default")
                .WithElement("Task", "sampleTask");
            // End snippet
        }

        [Fact]
        public void ArrayProperties()
        {
            // Snippet: ArrayProperties
            Entity entity = new Entity
            {
                ["tags"] = new ArrayValue { Values = { "fun", "programming" } },
                ["collaborators"] = new ArrayValue { Values = { "alice", "bob" } }
            };
            // End snippet
        }

        // Snippets ported from https://cloud.google.com/datastore/docs/concepts/queries

        [Fact(Skip = "Requires composite index configuration")]
        public void CompositeFilterQuery()
        {
            string projectId = _fixture.ProjectId;
            PartitionId partitionId = _fixture.PartitionId;

            // Snippet: CompositeFilter
            Query query = new Query
            {
                Kind = { "Task" },
                Filter = new Filter
                {
                    CompositeFilter = new CompositeFilter
                    {
                        Filters =
                        {
                            new Filter { PropertyFilter = new PropertyFilter { Op = Operator.Equal, Property = new PropertyReference("done"), Value = false } },
                            new Filter { PropertyFilter = new PropertyFilter { Op = Operator.GreaterThanOrEqual, Property = new PropertyReference("priority"), Value = 4 } },
                        },
                        Op = CompositeFilter.Types.Operator.And
                    }
                },
                Order = { new PropertyOrder { Direction = Direction.Descending, Property = new PropertyReference("priority") } }
            };

            DatastoreClient client = DatastoreClient.Create();
            RunQueryResponse response = client.RunQuery(projectId, partitionId, new ReadOptions { ReadConsistency = ReadConsistency.Eventual }, query);
            foreach (EntityResult result in response.Batch.EntityResults)
            {
                Entity entity = result.Entity;
                Console.WriteLine((string)entity["description"]);
            }
            // TODO: Results beyond this batch?
            // End snippet           
        }

        [Fact]
        public void KeyQuery()
        {
            string projectId = _fixture.ProjectId;
            string namespaceId = _fixture.NamespaceId;

            // Snippet: KeyQuery
            KeyFactory keyFactory = new KeyFactory(projectId, namespaceId, "Task");
            Query query = new Query
            {
                Kind = { "Task" },
                Filter = new Filter
                {
                    PropertyFilter = new PropertyFilter
                    {
                        Property = new PropertyReference(DatastoreConstants.KeyProperty),
                        Op = Operator.GreaterThan,
                        Value = keyFactory.CreateKey("someTask")
                    }
                }
            };
            // End snippet
        }
        
        [Fact]
        public void AncestorQuery()
        {
            string projectId = _fixture.ProjectId;
            string namespaceId = _fixture.NamespaceId;

            // Snippet: AncestorQuery
            KeyFactory keyFactory = new KeyFactory(projectId, namespaceId, "Task");
            Query query = new Query
            {
                Kind = { "Task" },
                Filter = new Filter
                {
                    PropertyFilter = new PropertyFilter
                    {
                        Property = new PropertyReference(DatastoreConstants.KeyProperty),
                        Op = Operator.HasAncestor,
                        Value = keyFactory.CreateKey("someTask")
                    }
                }
            };
            // End snippet
        }

        [Fact]
        public void KindlessQuery()
        {
            string projectId = _fixture.ProjectId;
            string namespaceId = _fixture.NamespaceId;

            // Snippet: KindlessQuery
            KeyFactory keyFactory = new KeyFactory(projectId, namespaceId, "Task");
            Key lastSeenKey = keyFactory.CreateKey(100L);
            Query query = new Query
            {
                Filter = new Filter
                {
                    PropertyFilter = new PropertyFilter
                    {
                        Property = new PropertyReference(DatastoreConstants.KeyProperty),
                        Op = Operator.GreaterThan,
                        Value = lastSeenKey,
                    }
                }
            };
            // End snippet
        }

        [Fact]
        public void KeysOnlyQuery()
        {
            // Snippet: KeysOnlyQuery
            Query query = new Query
            {
                Kind = { "Task" },
                Projection = { DatastoreConstants.KeyProperty }
            };
            // End snippet
        }

        [Fact(Skip = "Requires composite index configuration")]
        public void ProjectionQuery()
        {
            string projectId = _fixture.ProjectId;
            PartitionId partitionId = _fixture.PartitionId;

            // Snippet: ProjectionQuery
            Query query = new Query
            {
                Kind = { "Task" },
                Projection = { "priority", "percentage_complete" }
            };
            DatastoreClient client = DatastoreClient.Create();
            RunQueryResponse response = client.RunQuery(projectId, partitionId, new ReadOptions { ReadConsistency = ReadConsistency.Eventual }, query);
            foreach (EntityResult result in response.Batch.EntityResults)
            {
                Entity entity = result.Entity;
                Console.WriteLine($"{(int)entity["priority"]}: {(double?)entity["percentage_complete"]}");
            }
            // End snippet
        }

        [Fact]
        public void GroupingQuery()
        {
            // Snippet: GroupingQuery
            Query query = new Query
            {
                Kind = { "Task" },
                Projection = { "type", "priority" },
                DistinctOn = { new PropertyReference("type") },
                Order =
                {
                    new PropertyOrder { Property = new PropertyReference("type"), Direction = Direction.Ascending },
                    new PropertyOrder { Property = new PropertyReference("priority"), Direction = Direction.Ascending }
                }
            };
            // End snippet
        }

        [Fact]
        public void ArrayQueryComparison()
        {
            // Snippet: ArrayQuery
            Query query = new Query
            {
                Kind = { "Task" },
                Filter = new Filter
                {
                    CompositeFilter = new CompositeFilter
                    {
                        Filters =
                        {
                            new Filter { PropertyFilter = new PropertyFilter { Op = Operator.GreaterThan, Property = new PropertyReference("tag"), Value = "learn" } },
                            new Filter { PropertyFilter = new PropertyFilter { Op = Operator.LessThan, Property = new PropertyReference("tag"), Value = "math" } },
                        },
                        Op = CompositeFilter.Types.Operator.And
                    }
                },
            };
            // End snippet
        }

        [Fact]
        public void ArrayQueryEquality()
        {
            // Snippet: ArrayQuery
            Query query = new Query
            {
                Kind = { "Task" },
                Filter = new Filter
                {
                    CompositeFilter = new CompositeFilter
                    {
                        Filters =
                        {
                            new Filter { PropertyFilter = new PropertyFilter { Op = Operator.Equal, Property = new PropertyReference("tag"), Value = "fun" } },
                            new Filter { PropertyFilter = new PropertyFilter { Op = Operator.Equal, Property = new PropertyReference("tag"), Value = "programming" } },
                        },
                        Op = CompositeFilter.Types.Operator.And
                    }
                },
            };
            // End snippet
        }

        [Fact]
        public void PaginateWithCursor()
        {
            string projectId = _fixture.ProjectId;
            PartitionId partitionId = _fixture.PartitionId;

            ByteString pageCursor = null;
            int pageSize = 5;
            // Snippet: PaginateWithCursor
            Query query = new Query { Kind = { "Task" }, Limit = pageSize, StartCursor = pageCursor ?? ByteString.Empty };
            DatastoreClient client = DatastoreClient.Create();

            RunQueryResponse response = client.RunQuery(
                projectId, partitionId, new ReadOptions { ReadConsistency = ReadConsistency.Eventual }, query);
            foreach (EntityResult result in response.Batch.EntityResults)
            {
                Entity entity = result.Entity;
                // Do something with the task entity
            }
            ByteString nextPageCursor = response.Batch.EndCursor;
            // End snippet
        }

        [Fact]
        public void OrderingWithInequalityFilter()
        {
            // Snippet: OrderingWithInequalityFilter
            Query query = new Query
            {
                Kind = { "Task" },
                Filter = new Filter { PropertyFilter = new PropertyFilter { Property = new PropertyReference("priority"), Op = Operator.GreaterThan, Value = 3 } },
                Order =
                {
                    // This property must be sorted first, as it is in the inequality filter
                    new PropertyOrder { Property = new PropertyReference("priority"), Direction = Direction.Ascending },
                    new PropertyOrder { Property = new PropertyReference("created"), Direction = Direction.Ascending }
                }
            };
            // End snippet
        }

        // Snippets ported from https://cloud.google.com/datastore/docs/concepts/transactions

        [Fact]
        public void TransactionReadAndWrite()
        {
            string projectId = _fixture.ProjectId;
            long amount = 1000L;
            Key fromKey = CreateAccount("Jill", 20000L);
            Key toKey = CreateAccount("Beth", 15500L);

            // Snippet TransactionReadAndWrite
            DatastoreClient client = DatastoreClient.Create();
            ByteString transaction = client.BeginTransaction(projectId).Transaction;
            try
            {
                LookupResponse lookupResponse = client.Lookup(
                    projectId,
                    new ReadOptions { ReadConsistency = ReadConsistency.Strong, Transaction = transaction },
                    new[] { fromKey, toKey }
                );
                Entity from = lookupResponse.Found[0].Entity;
                Entity to = lookupResponse.Found[1].Entity;
                from["balance"] = (long)from["balance"] - amount;
                to["balance"] = (long)to["balance"] - amount;
                client.Commit(projectId, Mode.Transactional, transaction, new[] { from.ToUpdate(), to.ToUpdate() });
                transaction = null; // No need to roll back
            }
            finally
            {
                if (transaction != null)
                {
                    client.Rollback(projectId, transaction);
                }
            }
            // End snippet
        }

        // Used by TransactionReadAndWrite. Could promote to the fixture.
        private Key CreateAccount(string name, long balance)
        {
            string projectId = _fixture.ProjectId;
            string namespaceId = _fixture.NamespaceId;
            DatastoreClient client = DatastoreClient.Create();
            KeyFactory factory = new KeyFactory(projectId, namespaceId, "Account");
            Entity entity = new Entity
            {
                Key = factory.CreateInsertionKey(),
                ["name"] = name,
                ["balance"] = balance
            };
            CommitResponse response = client.Commit(projectId, Mode.NonTransactional, new[] { entity.ToInsert() });
            return response.MutationResults[0].Key;
        }
    }
}