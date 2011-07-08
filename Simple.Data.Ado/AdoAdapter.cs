﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Data;
using System.Data.Common;
using System.Data.SqlClient;
using System.Dynamic;
using System.Linq;
using System.Text;
using Simple.Data.Ado.Schema;

namespace Simple.Data.Ado
{
    [Export("Ado", typeof(Adapter))]
    public partial class AdoAdapter : Adapter, IAdapterWithRelation, IAdapterWithTransactions
    {
        private readonly AdoAdapterFinder _finder;
        private readonly ProviderHelper _providerHelper = new ProviderHelper();

        public ProviderHelper ProviderHelper
        {
            get { return _providerHelper; }
        }

        private IConnectionProvider _connectionProvider;

        public IConnectionProvider ConnectionProvider
        {
            get { return _connectionProvider; }
        }

        private DatabaseSchema _schema;
        private Lazy<AdoAdapterRelatedFinder> _relatedFinder;

        public AdoAdapter()
        {
            _finder = new AdoAdapterFinder(this);
        }

        internal AdoAdapter(IConnectionProvider connectionProvider) : this()
        {
            _connectionProvider = connectionProvider;
            _schema = DatabaseSchema.Get(_connectionProvider);
            _relatedFinder = new Lazy<AdoAdapterRelatedFinder>(CreateRelatedFinder);
        }

        protected override void OnSetup()
        {
            var settingsKeys = ((IDictionary<string, object>) Settings).Keys;
            if (settingsKeys.Contains("ConnectionString"))
            {
                if (settingsKeys.Contains("ProviderName"))
                {
                    _connectionProvider = ProviderHelper.GetProviderByConnectionString(Settings.ConnectionString,
                                                                                       Settings.ProviderName);
                }
                else
                {
                    _connectionProvider = ProviderHelper.GetProviderByConnectionString(Settings.ConnectionString);
                }
            }
            else if (settingsKeys.Contains("Filename"))
            {
                _connectionProvider = ProviderHelper.GetProviderByFilename(Settings.Filename);
            }
            else if (settingsKeys.Contains("ConnectionName"))
            {
                _connectionProvider = ProviderHelper.GetProviderByConnectionName(Settings.ConnectionName);
            }
            _schema = DatabaseSchema.Get(_connectionProvider);
            _relatedFinder = new Lazy<AdoAdapterRelatedFinder>(CreateRelatedFinder);
        }

        private AdoAdapterRelatedFinder CreateRelatedFinder()
        {
            return new AdoAdapterRelatedFinder(this);
        }

        public override IDictionary<string, object> FindOne(string tableName, SimpleExpression criteria)
        {
            return _finder.FindOne(tableName, criteria);
        }

        public override Func<object[],IDictionary<string,object>> CreateFindOneDelegate(string tableName, SimpleExpression criteria)
        {
            return _finder.CreateFindOneDelegate(tableName, criteria);
        }

        public override Func<object[], IEnumerable<IDictionary<string, object>>> CreateFindDelegate(string tableName, SimpleExpression criteria)
        {
            return _finder.CreateFindDelegate(tableName, criteria);
        }

        public override IEnumerable<IDictionary<string, object>> Find(string tableName, SimpleExpression criteria)
        {
            return _finder.Find(tableName, criteria);
        }

        public override IEnumerable<IDictionary<string, object>> RunQuery(SimpleQuery query)
        {
            var connection = _connectionProvider.CreateConnection();
            return new QueryBuilder(this).Build(query)
                .GetCommand(connection)
                .ToBufferedEnumerable(connection);
        }

        public override IDictionary<string, object> Insert(string tableName, IDictionary<string, object> data)
        {
            return new AdoAdapterInserter(this).Insert(tableName, data);
        }

        public override IEnumerable<IDictionary<string, object>> InsertMany(string tableName, IEnumerable<IDictionary<string, object>> data)
        {
            return new AdoAdapterInserter(this).InsertMany(tableName, data);
        }

        public IEnumerable<IDictionary<string, object>> InsertMany(string tableName, IEnumerable<IDictionary<string, object>> data, IAdapterTransaction transaction)
        {
            return new AdoAdapterInserter(this, ((AdoAdapterTransaction)transaction).Transaction).InsertMany(tableName, data);
        }

        public override int UpdateMany(string tableName, IEnumerable<IDictionary<string, object>> data)
        {
            var bulkUpdater = this.ProviderHelper.GetCustomProvider<IBulkUpdater>(this.ConnectionProvider) ?? new BulkUpdater();
            return bulkUpdater.Update(this, tableName, data.ToList(), null);
        }

        public int UpdateMany(string tableName, IEnumerable<IDictionary<string, object>> data, IAdapterTransaction transaction)
        {
            var bulkUpdater = this.ProviderHelper.GetCustomProvider<IBulkUpdater>(this.ConnectionProvider) ?? new BulkUpdater();
            return bulkUpdater.Update(this, tableName, data.ToList(), ((AdoAdapterTransaction)transaction).Transaction);
        }

        public override int UpdateMany(string tableName, IEnumerable<IDictionary<string, object>> data, IList<string> keyFields)
        {
            var bulkUpdater = this.ProviderHelper.GetCustomProvider<IBulkUpdater>(this.ConnectionProvider) ?? new BulkUpdater();
            return bulkUpdater.Update(this, tableName, data.ToList(), null);
        }

        public int UpdateMany(string tableName, IEnumerable<IDictionary<string, object>> data, IAdapterTransaction transaction, IList<string> keyFields)
        {
            var bulkUpdater = this.ProviderHelper.GetCustomProvider<IBulkUpdater>(this.ConnectionProvider) ?? new BulkUpdater();
            return bulkUpdater.Update(this, tableName, data.ToList(), ((AdoAdapterTransaction)transaction).Transaction);
        }

        public override int Update(string tableName, IDictionary<string, object> data, SimpleExpression criteria)
        {
            var commandBuilder = new UpdateHelper(_schema).GetUpdateCommand(tableName, data, criteria);
            return Execute(commandBuilder);
        }

        /// <summary>
        /// Deletes from the specified table.
        /// </summary>
        /// <param name="tableName">Name of the table.</param>
        /// <param name="criteria">The expression to use as criteria for the delete operation.</param>
        /// <returns>The number of records which were deleted.</returns>
        public override int Delete(string tableName, SimpleExpression criteria)
        {
            var commandBuilder = new DeleteHelper(_schema).GetDeleteCommand(tableName, criteria);
            return Execute(commandBuilder);
        }

        /// <summary>
        /// Gets the names of the fields which comprise the unique identifier for the specified table.
        /// </summary>
        /// <param name="tableName">Name of the table.</param>
        /// <returns>A list of field names; an empty list if no key is defined.</returns>
        public override IEnumerable<string> GetKeyFieldNames(string tableName)
        {
            return _schema.FindTable(tableName).PrimaryKey.AsEnumerable();
        }

        private int Execute(ICommandBuilder commandBuilder)
        {
            using (var connection = CreateConnection())
            {
                using (var command = commandBuilder.GetCommand(connection))
                {
                    connection.Open();
                    return TryExecute(command);
                }
            }
        }

        private static int Execute(ICommandBuilder commandBuilder, IAdapterTransaction transaction)
        {
            using (var command = commandBuilder.GetCommand(((AdoAdapterTransaction)transaction).Transaction.Connection))
            {
                return TryExecute(command);
            }
        }

        private static int TryExecute(IDbCommand command)
        {
            command.WriteTrace();
            try
            {
                return command.ExecuteNonQuery();
            }
            catch (DbException ex)
            {
                throw new AdoAdapterException(ex.Message, command);
            }
        }

        internal IDbConnection CreateConnection()
        {
            return _connectionProvider.CreateConnection();
        }

        internal DatabaseSchema GetSchema()
        {
            return DatabaseSchema.Get(_connectionProvider);
        }

        /// <summary>
        /// Determines whether a relation is valid.
        /// </summary>
        /// <param name="tableName">Name of the known table.</param>
        /// <param name="relatedTableName">Name of the table to test.</param>
        /// <returns>
        /// 	<c>true</c> if there is a valid relation; otherwise, <c>false</c>.
        /// </returns>
        public bool IsValidRelation(string tableName, string relatedTableName)
        {
            return _relatedFinder.Value.IsValidRelation(tableName, relatedTableName);
        }

        /// <summary>
        /// Finds data from a "table" related to the specified "table".
        /// </summary>
        /// <param name="tableName">Name of the table.</param>
        /// <param name="row"></param>
        /// <param name="relatedTableName"></param>
        /// <returns>The list of records matching the criteria. If no records are found, return an empty list.</returns>
        /// <remarks>When implementing the <see cref="Adapter"/> interface, if relationships are not possible, throw a <see cref="NotSupportedException"/>.</remarks>
        public object FindRelated(string tableName, IDictionary<string, object> row, string relatedTableName)
        {
            return _relatedFinder.Value.FindRelated(tableName, row, relatedTableName);
        }

        public IAdapterTransaction BeginTransaction()
        {
            var connection = CreateConnection();
            connection.Open();
            var transaction = connection.BeginTransaction();
            return new AdoAdapterTransaction(transaction);
        }

        public IAdapterTransaction BeginTransaction(IsolationLevel isolationLevel)
        {
            var connection = CreateConnection();
            connection.Open();
            var transaction = connection.BeginTransaction(isolationLevel);
            return new AdoAdapterTransaction(transaction);
        }

        public IAdapterTransaction BeginTransaction(string name)
        {
            var connection = CreateConnection();
            connection.Open();
            var sqlConnection = connection as SqlConnection;
            var transaction = sqlConnection != null ? sqlConnection.BeginTransaction(name) : connection.BeginTransaction();

            return new AdoAdapterTransaction(transaction, name);
        }

        public IAdapterTransaction BeginTransaction(IsolationLevel isolationLevel, string name)
        {
            var connection = CreateConnection();
            connection.Open();
            var sqlConnection = connection as SqlConnection;
            var transaction = sqlConnection != null
                                  ? sqlConnection.BeginTransaction(isolationLevel, name)
                                  : connection.BeginTransaction(isolationLevel);

            return new AdoAdapterTransaction(transaction, name);
        }

        public IEnumerable<IDictionary<string, object>> Find(string tableName, SimpleExpression criteria, IAdapterTransaction transaction)
        {
            return new AdoAdapterFinder(this, ((AdoAdapterTransaction)transaction).Transaction).Find(tableName, criteria);
        }

        public IDictionary<string, object> Insert(string tableName, IDictionary<string, object> data, IAdapterTransaction transaction)
        {
            return new AdoAdapterInserter(this, ((AdoAdapterTransaction)transaction).Transaction).Insert(tableName, data);
        }

        public int Update(string tableName, IDictionary<string, object> data, SimpleExpression criteria, IAdapterTransaction transaction)
        {
            var commandBuilder = new UpdateHelper(_schema).GetUpdateCommand(tableName, data, criteria);
            return Execute(commandBuilder, transaction);
        }

        public int Delete(string tableName, SimpleExpression criteria, IAdapterTransaction transaction)
        {
            var commandBuilder = new DeleteHelper(_schema).GetDeleteCommand(tableName, criteria);
            return Execute(commandBuilder, transaction);
        }

        public string GetIdentityFunction()
        {
            return _connectionProvider.GetIdentityFunction();
        }

        public bool ProviderSupportsCompoundStatements
        {
            get { return _connectionProvider.SupportsCompoundStatements; }
        }

        public ISchemaProvider SchemaProvider
        {
            get { return _connectionProvider.GetSchemaProvider(); }
        }
    }
}