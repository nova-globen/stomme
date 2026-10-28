using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Migrations.Operations.Builders;
using Stomme.Databases.Interfaces;
using Stomme.Interfaces;

// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable MemberCanBePrivate.Global

namespace Stomme.Migrations.EntityBuilders
{
    public abstract class DeletableAuditableBaseEntityBuilder<TEntityBuilder> : AuditableBaseEntityBuilder<TEntityBuilder> where TEntityBuilder : BaseEntityBuilder<TEntityBuilder>
    {
        protected DeletableAuditableBaseEntityBuilder(MigrationBuilder migrationBuilder, IDatabase database) : base(migrationBuilder, database)
        {
        }

        protected void AddDeletableColumns(ColumnsBuilder table)
        {
            DeletedBy = AddStringColumn(table,"DeletedBy", 256, true);
            DeletedOn = AddDateTimeColumn(table,"DeletedOn", true);
            IsDeleted = AddBooleanColumn(table,"IsDeleted");
        }

        public OperationBuilder<AddColumnOperation> DeletedBy { get; private set; }

        public OperationBuilder<AddColumnOperation> DeletedOn { get; private set; }

        public OperationBuilder<AddColumnOperation> IsDeleted { get; private set; }
    }
}
