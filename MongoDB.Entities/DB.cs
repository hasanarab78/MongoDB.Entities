﻿using System;
using System.Linq;
using MongoDB.Driver;
using MongoDB.Bson;
using MongoDB.Driver.Linq;
using System.Linq.Expressions;
using MongoDB.Bson.Serialization.Conventions;
using System.Threading.Tasks;
using System.Collections.Generic;
using MongoDB.Bson.Serialization;
using System.Reflection;
using MongoDB.Bson.Serialization.Serializers;
using System.Collections.ObjectModel;
using Microsoft.EntityFrameworkCore.Storage;

namespace MongoDB.Entities
{
    public class DB
    {
        protected private static IMongoDatabase db = null;

        /// <summary>
        /// Initializes the MongoDB connection with the given connection parameters.
        /// </summary>
        /// <param name="database">Name of the database</param>
        /// <param name="host">Adderss of the MongoDB server</param>
        /// <param name="port">Port number of the server</param>
        public DB(string database, string host = "127.0.0.1", int port = 27017)
        {
            Initialize(
                new MongoClientSettings { Server = new MongoServerAddress(host, port) }, database);
        }

        /// <summary>
        /// Initializes the MongoDB connection with an advanced set of parameters.
        /// </summary>
        /// <param name="settings">A MongoClientSettings object</param>
        /// <param name="database">Name of the database</param>
        public DB(MongoClientSettings settings, string database)
        {
            Initialize(settings, database);
        }

        private void Initialize(MongoClientSettings settings, string database)
        {
            if (db != null) throw new InvalidOperationException("Database connection is already initialized!");
            if (string.IsNullOrEmpty(database)) throw new ArgumentNullException("Database", "Database name cannot be empty!");

            try
            {
                db = new MongoClient(settings).GetDatabase(database);
                db.ListCollections().ToList().Count(); //get the collection count so that first db connection is established
            }
            catch (Exception)
            {
                db = null;
                throw;
            }

            BsonSerializer.RegisterSerializer(typeof(decimal), new DecimalSerializer(BsonType.Decimal128));
            BsonSerializer.RegisterSerializer(typeof(decimal?), new NullableSerializer<decimal>(new DecimalSerializer(BsonType.Decimal128)));

            ConventionRegistry.Register(
                "IgnoreExtraElements",
                new ConventionPack { new IgnoreExtraElementsConvention(true) },
                type => true);

            ConventionRegistry.Register(
                "IgnoreManyProperties",
                new ConventionPack { new IgnoreManyPropertiesConvention() },
                type => true);
        }

        internal static string GetCollectionName<T>()
        {
            string collection = typeof(T).Name;

            var attribute = typeof(T).GetTypeInfo().GetCustomAttribute<NameAttribute>();
            if (attribute != null)
            {
                collection = attribute.Name;
            }

            if (string.IsNullOrWhiteSpace(collection) || collection.Contains("~")) throw new ArgumentException("This is an illegal name for a collection!");

            return collection;
        }

        internal static IMongoCollection<JoinRecord> GetRefCollection(string name)
        {
            CheckIfInitialized();
            return db.GetCollection<JoinRecord>(name);
        }

        internal static IMongoClient GetClient()
        {
            CheckIfInitialized();
            return db.Client;
        }

        internal async static Task CreateIndexAsync<T>(CreateIndexModel<T> model)
        {
            await Collection<T>().Indexes.CreateOneAsync(model);
        }

        internal async static Task DropIndexAsync<T>(string name)
        {
            await Collection<T>().Indexes.DropOneAsync(name);
        }

        async internal static Task UpdateAsync<T>(FilterDefinition<T> filter, UpdateDefinition<T> definition, UpdateOptions options, IClientSessionHandle session = null)
        {
            await (session == null
                   ? Collection<T>().UpdateManyAsync(filter, definition, options)
                   : Collection<T>().UpdateManyAsync(session, filter, definition, options));
        }

        async internal static Task<List<TProjection>> FindAsync<T, TProjection>(FilterDefinition<T> filter, FindOptions<T, TProjection> options, IClientSessionHandle session = null)
        {
            return await (session == null
                ? (await Collection<T>().FindAsync(filter, options)).ToListAsync()
                : (await Collection<T>().FindAsync(session, filter, options)).ToListAsync());
        }

        /// <summary>
        /// Gets the IMongoCollection for a given Entity type.
        /// <para>TIP: Try never to use this unless really neccessary.</para>
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        public static IMongoCollection<T> Collection<T>()
        {
            CheckIfInitialized();
            return db.GetCollection<T>(GetCollectionName<T>());
        }

        /// <summary>
        /// Exposes the MongoDB collection for the given Entity as an IQueryable in order to facilitate LINQ queries.
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        public static IMongoQueryable<T> Queryable<T>(AggregateOptions options = null) => Collection<T>().AsQueryable(options);

        /// <summary>
        /// Exposes the MongoDB collection for the given Entity as an IAggregateFluent in order to facilitate Fluent queries.
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        /// <param name="options">The options for the aggregation. This is not required.</param>
        /// <param name="session">An optional session if using within a transaction</param>
        /// <returns></returns>
        public static IAggregateFluent<T> Fluent<T>(AggregateOptions options = null, IClientSessionHandle session = null)
        {
            return session == null
                   ? Collection<T>().Aggregate(options)
                   : Collection<T>().Aggregate(session, options);
        }

        /// <summary>
        /// Persists an entity to MongoDB
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        /// <param name="entity">The instance to persist</param>
        /// <param name="session">An optional session if using within a transaction</param>
        public static void Save<T>(T entity, IClientSessionHandle session = null) where T : Entity
        {
            SaveAsync<T>(entity, session).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Persists an entity to MongoDB
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        /// <param name="entity">The instance to persist</param>
        /// <param name="session">An optional session if using within a transaction</param>
        async public static Task SaveAsync<T>(T entity, IClientSessionHandle session = null) where T : Entity
        {
            if (string.IsNullOrEmpty(entity.ID)) entity.ID = ObjectId.GenerateNewId().ToString();
            entity.ModifiedOn = DateTime.UtcNow;

            await (session == null
                   ? Collection<T>().ReplaceOneAsync(x => x.ID.Equals(entity.ID), entity, new UpdateOptions() { IsUpsert = true })
                   : Collection<T>().ReplaceOneAsync(session, x => x.ID.Equals(entity.ID), entity, new UpdateOptions() { IsUpsert = true }));
        }

        /// <summary>
        /// Persists multiple entities to MongoDB in a single bulk operation
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        /// <param name="entities">The entities to persist</param>
        /// <param name="session">An optional session if using within a transaction</param>
        public static void Save<T>(IEnumerable<T> entities, IClientSessionHandle session = null) where T : Entity
        {
            SaveAsync<T>(entities, session).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Persists multiple entities to MongoDB in a single bulk operation
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        /// <param name="entities">The entities to persist</param>
        /// <param name="session">An optional session if using within a transaction</param>
        async public static Task SaveAsync<T>(IEnumerable<T> entities, IClientSessionHandle session = null) where T : Entity
        {
            var models = new Collection<WriteModel<T>>();
            foreach (var ent in entities)
            {
                if (string.IsNullOrEmpty(ent.ID)) ent.ID = ObjectId.GenerateNewId().ToString();
                ent.ModifiedOn = DateTime.UtcNow;

                var upsert = new ReplaceOneModel<T>(
                        filter: Builders<T>.Filter.Eq(e => e.ID, ent.ID),
                        replacement: ent)
                { IsUpsert = true };
                models.Add(upsert);
            }

            await (session == null
                   ? Collection<T>().BulkWriteAsync(models)
                   : Collection<T>().BulkWriteAsync(session, models));
        }

        /// <summary>
        /// Deletes a single entity from MongoDB.
        /// <para>HINT: If this entity is referenced by one-to-many/many-to-many relationships, those references are also deleted.</para>
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        /// <param name="ID">The Id of the entity to delete</param>
        /// <param name = "session" > An optional session if using within a transaction</param>
        public static void Delete<T>(string ID, IClientSessionHandle session = null) where T : Entity
        {
            DeleteAsync<T>(ID, session).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Deletes a single entity from MongoDB.
        /// <para>HINT: If this entity is referenced by one-to-many/many-to-many relationships, those references are also deleted.</para>
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        /// <param name="ID">The Id of the entity to delete</param>
        /// <param name = "session" > An optional session if using within a transaction</param>
        async public static Task DeleteAsync<T>(string ID, IClientSessionHandle session = null) where T : Entity
        {
            CheckIfInitialized();
            var joinCollections = (await db.ListCollectionNames().ToListAsync())
                                    .Where(c => 
                                           c.Contains("~") && 
                                           c.Contains(GetCollectionName<T>()));

            var tasks = new List<Task>();

            foreach (var cName in joinCollections)
            {
                tasks.Add(session == null
                          ? db.GetCollection<JoinRecord>(cName).DeleteManyAsync(r => r.ChildID.Equals(ID) || r.ParentID.Equals(ID))
                          : db.GetCollection<JoinRecord>(cName).DeleteManyAsync(session, r => r.ChildID.Equals(ID) || r.ParentID.Equals(ID)));
            }

            tasks.Add(session == null
                      ? Collection<T>().DeleteOneAsync(x => x.ID.Equals(ID))
                      : Collection<T>().DeleteOneAsync(session, x => x.ID.Equals(ID)));

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Deletes matching entities from MongoDB
        /// <para>HINT: If these entities are referenced by one-to-many/many-to-many relationships, those references are also deleted.</para>
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        /// <param name="expression">A lambda expression for matching entities to delete.</param>
        /// <param name = "session" > An optional session if using within a transaction</param>
        public static void Delete<T>(Expression<Func<T, bool>> expression, IClientSessionHandle session = null) where T : Entity
        {
            DeleteAsync<T>(expression, session).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Deletes matching entities from MongoDB
        /// <para>HINT: If these entities are referenced by one-to-many/many-to-many relationships, those references are also deleted.</para>
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        /// <param name="expression">A lambda expression for matching entities to delete.</param>
        /// <param name = "session" > An optional session if using within a transaction</param>
        async public static Task DeleteAsync<T>(Expression<Func<T, bool>> expression, IClientSessionHandle session = null) where T : Entity
        {
            var IDs = await Queryable<T>()
                              .Where(expression)
                              .Select(e => e.ID)
                              .ToListAsync();

            foreach (var id in IDs)
            {
                await DeleteAsync<T>(id, session);
            }
        }

        /// <summary>
        /// Deletes matching entities from MongoDB
        /// <para>HINT: If these entities are referenced by one-to-many/many-to-many relationships, those references are also deleted.</para>
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        /// <param name="IDs">An IEnumerable of entity IDs</param>
        /// <param name = "session" > An optional session if using within a transaction</param>
        public static void Delete<T>(IEnumerable<string> IDs, IClientSessionHandle session = null) where T : Entity
        {
            DeleteAsync<T>(IDs, session).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Deletes matching entities from MongoDB
        /// <para>HINT: If these entities are referenced by one-to-many/many-to-many relationships, those references are also deleted.</para>
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        /// <param name="IDs">An IEnumerable of entity IDs</param>
        /// <param name = "session" > An optional session if using within a transaction</param>
        async public static Task DeleteAsync<T>(IEnumerable<string> IDs, IClientSessionHandle session = null) where T : Entity
        {
            foreach (var id in IDs)
            {
                await DeleteAsync<T>(id, session);
            }
        }

        /// <summary>
        /// Represents an index for a given Entity
        /// <para>TIP: Define the keys first with .Key() method and finally call the .Create() method.</para>
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        public static Index<T> Index<T>() where T : Entity
        {
            return new Index<T>();
        }

        /// <summary>
        /// Search the text index of a collection for Entities matching the search term.
        /// <para>TIP: Make sure to define a text index with DB.Index&lt;T&gt;() before searching</para>
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        /// <param name="searchTerm">The text to search the index for</param>
        /// <param name="caseSensitive">Set true to do a case sensitive search</param>
        /// <param name="options">Options for finding documents (not required)</param>
        /// <param name = "session" > An optional session if using within a transaction</param>
        /// <returns>A List of Entities of given type</returns>
        public static List<T> SearchText<T>(string searchTerm, bool caseSensitive = false, FindOptions<T, T> options = null, IClientSessionHandle session = null)
        {
            return SearchTextAsync<T>(searchTerm, caseSensitive, options).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Search the text index of a collection for Entities matching the search term.
        /// <para>TIP: Make sure to define a text index with DB.Index&lt;T&gt;() before searching</para>
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        /// <param name="searchTerm">The text to search the index for</param>
        /// <param name="caseSensitive">Set true to do a case sensitive search</param>
        /// <param name="options">Options for finding documents (not required)</param>
        /// <param name = "session" >An optional session if using within a transaction</param>
        /// <returns>A List of Entities of given type</returns>
        async public static Task<List<T>> SearchTextAsync<T>(string searchTerm, bool caseSensitive = false, FindOptions<T, T> options = null, IClientSessionHandle session = null)
        {
            var filter = Builders<T>.Filter.Text(searchTerm, new TextSearchOptions { CaseSensitive = caseSensitive });
            return await (session == null
                          ? (await Collection<T>().FindAsync(filter, options)).ToListAsync()
                          : (await Collection<T>().FindAsync(session, filter, options)).ToListAsync());
        }

        /// <summary>
        /// Represents a batch update command
        /// <para>TIP: Specify a filter first with the .Match() method. Then set property values with .Set() and finally call .Execute() to run the command.</para>
        /// </summary>
        /// <typeparam name="T">Any class that inhertis from Entity</typeparam>
        public static Update<T> Update<T>() where T : Entity
        {
            return new Update<T>();
        }

        /// <summary>
        /// Represents a MongoDB Find command
        /// <para>TIP: Specify your criteria using .Match() .Sort() .Skip() .Take() .Project() .Option() methods and finally call .Execute()</para>
        /// </summary>
        /// <typeparam name="T">Any class that inhertis from Entity</typeparam>
        public static Find<T> Find<T>() where T : Entity
        {
            return new Find<T>();
        }

        /// <summary>
        /// Represents a MongoDB Find command
        /// <para>TIP: Specify your criteria using .Match() .Sort() .Skip() .Take() .Project() .Option() methods and finally call .Execute()</para>
        /// </summary>
        /// <typeparam name="T">Any class that inhertis from Entity</typeparam>
        /// <typeparam name="TProjection">The type that is returned by projection</typeparam>
        /// <returns></returns>
        public static Find<T, TProjection> Find<T, TProjection>() where T : Entity
        {
            return new Find<T, TProjection>();
        }

        /// <summary>
        /// Start a fluent aggregation pipeline with a $GeoNear stage with the supplied parameters.
        /// </summary>
        /// <param name="NearCoordinates">The coordinates from which to find documents from</param>
        /// <param name="DistanceField">x => x.Distance</param>
        /// <param name="Spherical">Calculate distances using spherical geometry or not</param>
        /// <param name="MaxDistance">The maximum distance from the center point that the documents can be</param>
        /// <param name="MinDistance">The minimum distance from the center point that the documents can be</param>
        /// <param name="Limit">The maximum number of documents to return</param>
        /// <param name="Query">Limits the results to the documents that match the query</param>
        /// <param name="DistanceMultiplier">The factor to multiply all distances returned by the query</param>
        /// <param name="IncludeLocations">Specify the output field to store the point used to calculate the distance</param>
        /// <param name="IndexKey"></param>
        /// <param name="options">The options for the aggregation. This is not required.</param>
        /// <param name="session">An optional session if using within a transaction</param>
        public static IAggregateFluent<T> GeoNear<T>(Coordinates2D NearCoordinates, Expression<Func<T, object>> DistanceField, bool Spherical = true, int? MaxDistance = null, int? MinDistance = null, int? Limit = null, BsonDocument Query = null, int? DistanceMultiplier = null, Expression<Func<T, object>> IncludeLocations = null, string IndexKey = null, AggregateOptions options = null, IClientSessionHandle session = null) where T : Entity
        {
            return (new GeoNear<T>
            {
                near = NearCoordinates,
                distanceField = DistanceField.FullPath(),
                spherical = Spherical,
                maxDistance = MaxDistance,
                minDistance = MinDistance,
                query = Query,
                distanceMultiplier = DistanceMultiplier,
                limit = Limit,
                includeLocs = IncludeLocations.FullPath(),
                key = IndexKey,
            })
            .ToFluent(options, session);
        }

        /// <summary>
        /// Returns a new instance of the supplied Entity type
        /// </summary>
        /// <typeparam name="T">Any class that inherits from Entity</typeparam>
        /// <returns></returns>
        public static T Entity<T>() where T : Entity, new()
        {
            return new T();
        }

        private static void CheckIfInitialized()
        {
            if (db == null) throw new InvalidOperationException("Database connection is not initialized!");
        }
    }

    internal class IgnoreManyPropertiesConvention : ConventionBase, IMemberMapConvention
    {
        public void Apply(BsonMemberMap mMap)
        {
            if (mMap.MemberType.Name == "Many`1")
            {
                mMap.SetShouldSerializeMethod(o => false);
            }
        }
    }
}
