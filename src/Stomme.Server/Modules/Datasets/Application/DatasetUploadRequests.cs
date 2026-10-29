#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Mediator;
using Stomme.Security.Abstractions;
using FluentValidation;

namespace Stomme.Modules.Datasets.Application
{
    /// <summary>
    /// Registers a dataset from a file the framework's file manager holds: the file is opened
    /// as the CURRENT user through <see cref="IManagedFileReader"/> (another site's file does not exist, a folder the user
    /// may not view is refused, a file over the upload cap is not read), parsed by the hardened
    /// <see cref="UploadedDatasetParser"/> (the format from the signature and the extension, a bounded sample, the FULL row
    /// count), then handed to the registrar under the origin <c>file:&lt;id&gt;</c> - so the same file registered again
    /// refreshes its one row. The name defaults to the file's stem.
    /// </summary>
    [Requires(DatasetPermissions.Manage)]
    public sealed record RegisterUploadedDatasetCommand(int FileId, string? Name = null, DataClassification Classification = DataClassification.Internal) : ICommand<Guid>;

    public sealed class RegisterUploadedDatasetCommandValidator : AbstractValidator<RegisterUploadedDatasetCommand>
    {
        public RegisterUploadedDatasetCommandValidator()
        {
            RuleFor(command => command.FileId).GreaterThan(0);
            RuleFor(command => command.Name).MaximumLength(DatasetLimits.MaxNameLength);
        }
    }

    public sealed class RegisterUploadedDatasetCommandHandler(IUploadedDatasetSource source, IDatasetRegistrar registrar) : ICommandHandler<RegisterUploadedDatasetCommand, Guid>
    {
        public async Task<Guid> Handle(RegisterUploadedDatasetCommand request, CancellationToken ct)
        {
            var content = await source.ReadAsync(request.FileId, ct);
            var name = string.IsNullOrWhiteSpace(request.Name) ? content.SuggestedName : request.Name.Trim();
            var rowCount = content.RowCount > int.MaxValue ? int.MaxValue : (int)content.RowCount;
            return await registrar.RegisterOrRefreshAsync(
                new DatasetRegistration(name, OriginKind.Upload, OriginReference.ForFile(request.FileId), content.SampleJson, request.Classification, rowCount), ct);
        }
    }

    /// <summary>The origin reference an uploaded file registers under: <c>file:&lt;id&gt;</c>.</summary>
    public static class OriginReference
    {
        public static string ForFile(int fileId) => "file:" + fileId;
    }
}
