#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Stomme.Datasets;
using Stomme.Modules.Datasets.Application;
using Stomme.Modules.Datasets.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Datasets
{
    /// <summary>
    /// The predecessor platform's DatasetTests by name where they are the domain's (the inference, the masked
    /// summary, the escalate-only refresh) on the reshaped aggregate, plus the port's own rules: the id is the ORIGIN's,
    /// the name and the reference are normalised, the sample is bounded and must be a JSON array, the schema and
    /// the summary are one JSON value each and read back typed, the summary must describe the schema, and the ONE
    /// classification order (Q1). the predecessor platform's mediator, registrar and upload cases are increment 10's.
    /// </summary>
    public sealed class DatasetTests
    {
        private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        private static DatasetAggregate Register(string name, OriginKind kind, string? reference, DataClassification classification, string sampleJson, int siteId = 1)
        {
            var inference = SchemaInference.Infer(sampleJson);
            return DatasetAggregate.Register(TenantId.Default, siteId, name, kind, reference, classification, inference.Schema, inference.Summary, inference.SampleRowsJson, Now);
        }

        [Fact]
        public void Inference_InfersTypesAndSummary()
        {
            var result = SchemaInference.Infer("[ { \"age\": \"30\", \"name\": \"Ada\", \"joined\": \"2026-01-02\", \"active\": \"true\", \"amount\": \"9.5\", \"meta\": \"{\\\"a\\\":1}\" }, { \"age\": \"40\", \"name\": \"Bo\", \"joined\": \"2026-03-04\", \"active\": \"false\", \"amount\": \"1\", \"meta\": \"[]\" } ]");

            var columns = result.Schema.Columns.ToDictionary(column => column.Name);
            Assert.Equal((ColumnDataType.Number, ColumnDataType.String, ColumnDataType.Date, ColumnDataType.Boolean, ColumnDataType.Number, ColumnDataType.Json),
                (columns["age"].DataType, columns["name"].DataType, columns["joined"].DataType, columns["active"].DataType, columns["amount"].DataType, columns["meta"].DataType));
            Assert.Equal("currency", columns["amount"].SemanticType);
            var age = result.Summary.Columns.First(column => column.Name == "age");
            Assert.Equal(("30", "40", 2, 0), (age.Min, age.Max, age.UniqueCount, age.NullCount));
            Assert.Equal(new[] { "30", "40" }, age.Samples);
            Assert.Equal(2, result.Summary.RowCountEstimate);
            Assert.Equal(2, JsonDocument.Parse(result.SampleRowsJson).RootElement.GetArrayLength());
        }

        [Fact]
        public void Inference_KeepsTheBoundedSample_AndTheFullRowCount_AndRefusesAnOverCapSample()
        {
            var rows = string.Join(",", Enumerable.Range(1, 120).Select(i => $"{{ \"n\": \"{i}\" }}"));
            var result = SchemaInference.Infer("[" + rows + "]");
            Assert.Equal(120, result.Summary.RowCountEstimate);
            Assert.Equal(DatasetLimits.MaxSampleRows, JsonDocument.Parse(result.SampleRowsJson).RootElement.GetArrayLength());

            Assert.Empty(SchemaInference.Infer("not json").Schema.Columns);
            Assert.Empty(SchemaInference.Infer(null).Schema.Columns);
            var ex = Assert.Throws<ArgumentException>(() => SchemaInference.Infer("[" + new string(' ', DatasetLimits.MaxSampleBytes) + "]"));
            Assert.Contains("bytes", ex.Message);
        }

        [Fact]
        public void Summary_Masked_StripsValues()
        {
            foreach (var sensitive in new[] { DataClassification.Confidential, DataClassification.Restricted })
            {
                var dataset = Register("secret", OriginKind.Upload, null, sensitive, "[ { \"ssn\": \"123-45-6789\" } ]");
                Assert.True(dataset.IsSensitive);
                Assert.False(dataset.RawRowsAllowed);
                Assert.All(dataset.PublishedSummary().Columns, column => Assert.Empty(column.Samples));
                Assert.All(dataset.PublishedSummary().Columns, column => Assert.Null(column.Min));
                Assert.Equal(1, dataset.PublishedSummary().Columns.Single().UniqueCount); // the counts survive masking
                Assert.DoesNotContain("123-45-6789", dataset.ToDescriptor().Summary.Columns.SelectMany(column => column.Samples));
            }

            foreach (var open in new[] { DataClassification.Public, DataClassification.Internal })
            {
                var dataset = Register("open", OriginKind.Upload, null, open, "[ { \"sku\": \"A1\" } ]");
                Assert.False(dataset.IsSensitive);
                Assert.True(dataset.RawRowsAllowed);
                Assert.Equal(new[] { "A1" }, dataset.PublishedSummary().Columns.Single().Samples);
            }
        }

        [Fact]
        public void The_one_classification_order_is_Public_Internal_Confidential_Restricted()
        {
            Assert.True(DataClassification.Public < DataClassification.Internal);
            Assert.True(DataClassification.Internal < DataClassification.Confidential);
            Assert.True(DataClassification.Confidential < DataClassification.Restricted); // Restricted is PII, the top (the predecessor platform ranked it below Confidential)
            Assert.Equal(new[] { false, false, true, true }, Enum.GetValues<DataClassification>().Select(DatasetClassificationPolicy.IsSensitive));
        }

        [Fact]
        public void Refresh_EscalatesClassification_AndRaisesReclassifiedEvent()
        {
            var dataset = Register("Crawl", OriginKind.PipelineOutput, "p1", DataClassification.Internal, "[ { \"a\": \"1\" } ]");
            dataset.ClearDomainEvents();

            var next = SchemaInference.Infer("[ { \"a\": \"2\" } ]"); // the same column and type: no schema change, only the verdict
            dataset.Refresh(next.Schema, next.Summary, next.SampleRowsJson, DataClassification.Restricted, Now.AddHours(1));

            Assert.Equal(DataClassification.Restricted, dataset.Classification);
            var reclassified = Assert.Single(dataset.DomainEvents.OfType<DatasetReclassifiedEvent>());
            Assert.Equal((DataClassification.Internal, DataClassification.Restricted), (reclassified.From, reclassified.To));
            Assert.Single(dataset.DomainEvents.OfType<DatasetRefreshedEvent>());
            Assert.Empty(dataset.DomainEvents.OfType<DatasetSchemaChangedEvent>()); // same columns, a different value
            Assert.Equal(Now.AddHours(1), dataset.LastRefreshedUtc);
            Assert.True(dataset.IsSensitive);
        }

        [Fact]
        public void Refresh_DoesNotDowngradeClassification()
        {
            var dataset = Register("Secrets", OriginKind.Upload, null, DataClassification.Confidential, "[ { \"a\": \"1\" } ]");
            dataset.ClearDomainEvents();

            var next = SchemaInference.Infer("[ { \"a\": \"2\", \"b\": \"3\" } ]");
            dataset.Refresh(next.Schema, next.Summary, next.SampleRowsJson, DataClassification.Internal, Now.AddHours(1));

            Assert.Equal(DataClassification.Confidential, dataset.Classification);
            Assert.Empty(dataset.DomainEvents.OfType<DatasetReclassifiedEvent>());
            Assert.Single(dataset.DomainEvents.OfType<DatasetSchemaChangedEvent>()); // a column was added
            Assert.Equal(new[] { "a", "b" }, dataset.Schema.ColumnNames);
        }

        [Fact]
        public void Register_DerivesTheIdFromTheOrigin_AndNormalisesTheNameAndTheReference()
        {
            var first = Register("Sales", OriginKind.PipelineOutput, "Pipeline-7", DataClassification.Internal, "[ { \"a\": \"1\" } ]");
            var again = Register("sales renamed", OriginKind.PipelineOutput, " pipeline-7 ", DataClassification.Internal, "[ { \"a\": \"2\" } ]");
            var elsewhere = Register("Sales", OriginKind.PipelineOutput, "Pipeline-7", DataClassification.Internal, "[ { \"a\": \"1\" } ]", siteId: 2);
            var otherKind = Register("Sales", OriginKind.Upload, "Pipeline-7", DataClassification.Internal, "[ { \"a\": \"1\" } ]");

            Assert.Equal(first.Id, again.Id); // one origin, one id - whatever the case, the padding or the name
            Assert.NotEqual(first.Id, elsewhere.Id);
            Assert.NotEqual(first.Id, otherKind.Id);
            Assert.Equal(("Sales", "SALES", "Pipeline-7", "PIPELINE-7"), (first.Name, first.NameNormalized, first.OriginReference, first.OriginReferenceNormalized));
            Assert.Equal(DatasetIdentity.For(TenantId.Default, 1, OriginKind.PipelineOutput, "PIPELINE-7"), first.Id);
            // Another TENANT on the same site id and origin is another id: the module store is one per installation (BL-94 increment 14 found the collision).
            Assert.NotEqual(first.Id, DatasetIdentity.For(TenantId.From(Guid.NewGuid()), 1, OriginKind.PipelineOutput, "PIPELINE-7"));
            Assert.Single(first.DomainEvents.OfType<DatasetRegisteredEvent>());

            // Without a reference the name stands in for it, so an upload registered under one name is one row.
            var unnamedOrigin = Register("Uploaded Sales", OriginKind.Upload, null, DataClassification.Internal, "[ { \"a\": \"1\" } ]");
            Assert.Null(unnamedOrigin.OriginReference);
            Assert.Equal("UPLOADED SALES", unnamedOrigin.OriginReferenceNormalized);
            Assert.Equal(DatasetIdentity.For(TenantId.Default, 1, OriginKind.Upload, "uploaded sales"), unnamedOrigin.Id);
        }

        [Fact]
        public void Register_RefusesABlankName_AnOverLongName_ANonArraySample_AndAnOverCapSample()
        {
            var inference = SchemaInference.Infer("[ { \"a\": \"1\" } ]");
            Assert.Throws<ArgumentException>(() => DatasetAggregate.Register(TenantId.Default, 1, "  ", OriginKind.Upload, null, DataClassification.Internal, inference.Schema, inference.Summary, inference.SampleRowsJson, Now));
            Assert.Throws<ArgumentException>(() => DatasetAggregate.Register(TenantId.Default, 1, new string('n', DatasetLimits.MaxNameLength + 1), OriginKind.Upload, null, DataClassification.Internal, inference.Schema, inference.Summary, inference.SampleRowsJson, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => DatasetAggregate.Register(TenantId.Default, 0, "x", OriginKind.Upload, null, DataClassification.Internal, inference.Schema, inference.Summary, inference.SampleRowsJson, Now));
            Assert.Throws<ArgumentException>(() => DatasetAggregate.Register(TenantId.Default, 1, "x", OriginKind.Upload, null, DataClassification.Internal, inference.Schema, inference.Summary, "{ \"not\": \"an array\" }", Now));
            var tooMany = "[" + string.Join(",", Enumerable.Range(0, DatasetLimits.MaxSampleRows + 1).Select(_ => "{}")) + "]";
            Assert.Throws<ArgumentException>(() => DatasetAggregate.Register(TenantId.Default, 1, "x", OriginKind.Upload, null, DataClassification.Internal, inference.Schema, inference.Summary, tooMany, Now));
        }

        [Fact]
        public void A_summary_that_does_not_describe_the_schema_is_refused()
        {
            var schema = DatasetSchema.From(new[] { new DatasetColumn("a", ColumnDataType.String) });
            var summary = DatasetSummary.From(new[] { new DatasetColumnSummary("b", Array.Empty<string>(), null, null, 0, 0) }, 0);
            Assert.Throws<InvalidOperationException>(() => DatasetAggregate.Register(TenantId.Default, 1, "x", OriginKind.Upload, null, DataClassification.Internal, schema, summary, "[]", Now));
            Assert.Throws<ArgumentException>(() => DatasetSchema.From(new[] { new DatasetColumn("a", ColumnDataType.String), new DatasetColumn("a", ColumnDataType.Number) }));
        }

        [Fact]
        public void The_schema_and_the_summary_are_one_json_value_each_and_read_back_typed()
        {
            var schema = DatasetSchema.From(new[] { new DatasetColumn("amount", ColumnDataType.Number, "currency", "Euro amount"), new DatasetColumn("when", ColumnDataType.Date) });
            var summary = DatasetSummary.From(new[] { new DatasetColumnSummary("amount", new[] { "1", "2" }, "1", "2", 2, 0), new DatasetColumnSummary("when", Array.Empty<string>(), null, null, 0, 2) }, 2);

            var schemaBack = DatasetSchema.Create(schema.Value);
            var summaryBack = DatasetSummary.Create(summary.Value);

            Assert.Equal(schema, schemaBack);
            Assert.Equal(new DatasetColumn("amount", ColumnDataType.Number, "currency", "Euro amount"), schemaBack.Columns[0]);
            Assert.Contains("\"Number\"", schema.Value); // the enum by name, so a stored value reads without the port's numbering
            Assert.Equal(summary, summaryBack);
            Assert.Equal((2, "1", "2", 2), (summaryBack.RowCountEstimate, summaryBack.Columns[0].Min, summaryBack.Columns[0].Max, summaryBack.Columns[0].Samples.Count));
            Assert.Empty(DatasetSchema.Create("").Columns);
            Assert.Empty(DatasetSummary.Create("garbage").Columns);
        }
    }
}
