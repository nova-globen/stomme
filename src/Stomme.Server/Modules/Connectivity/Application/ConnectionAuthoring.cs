#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Connectivity;
using Stomme.Modules.Connectivity.Domain;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using FluentValidation;
using FluentValidation.Results;

namespace Stomme.Modules.Connectivity.Application
{
    /// <summary>The checks a create and an update share (BL-94 increment 6, D110): the HostOnly gate, the schema, and the domain's own refusals BEFORE a secret is written.</summary>
    internal static class ConnectionAuthoring
    {
        /// <summary>A connector that reaches the host itself (a process, the file system) is created or edited by the Host role alone (D110).</summary>
        public static void RequireHostIfHostOnly(IConnector connector, ICurrentUser user)
        {
            if (connector.HostOnly && !user.Roles.Contains(RoleNames.Host))
            {
                throw new UnauthorizedAccessException($"Connector '{connector.ConnectorType}' reaches the host itself; only the Host role may create or edit a connection of this type.");
            }
        }

        /// <summary>The connector's schema, as a validation failure the API answers as the caller's input.</summary>
        public static void ValidateSettings(IConnector connector, IReadOnlyDictionary<string, string>? settings)
        {
            var errors = connector.ConfigSchema.Validate(settings ?? new Dictionary<string, string>(StringComparer.Ordinal));
            if (errors.Count > 0)
            {
                throw new ValidationException(errors.Select(error => new ValidationFailure("Settings", error)).ToList());
            }
        }

        /// <summary>The domain's refusals (an endpoint carrying a credential, over-long settings) are raised here, so a refused request writes no secret.</summary>
        public static void Preflight(IConnector connector, string endpoint, IReadOnlyDictionary<string, string>? settings)
        {
            ConnectionEndpoint.For(connector.EndpointKind, endpoint);
            ConnectionSettings.From(settings);
        }
    }

    /// <summary>The connector catalog projected for the editor: the schema, the scheme's credential fields, the categories, the operations.</summary>
    internal static class ConnectorViews
    {
        public static ConnectorView From(IConnector connector) => new(
            connector.ConnectorType,
            connector.DisplayName,
            connector.AuthType.ToString(),
            connector.EndpointKind.ToString(),
            connector.HostOnly,
            connector.ConfigSchema.Fields.Select(Field).ToList(),
            AuthenticationScheme.FieldsFor(connector.AuthType).Select(field => new ConnectorAuthFieldView(field.Name, connector.CredentialLabels?.GetValueOrDefault(field.Name) ?? field.DisplayName, field.Secret)).ToList(),
            Categories(connector),
            connector is IConnectorExtractor extractor
                ? extractor.Operations.Select(operation => new ConnectorOperationView(operation.Name, operation.DisplayName, operation.Description, operation.Parameters.Fields.Select(Field).ToList())).ToList()
                : new List<ConnectorOperationView>(),
            connector.WebsiteUrl?.ToString(),
            connector.ApiDocumentationUrl?.ToString(),
            ConnectorIcons.For(connector))
        {
            EndpointExample = connector.EndpointExample,
        };

        private static ConnectorFieldView Field(ConnectorConfigField field) => new(
            field.Name,
            field.DisplayName,
            field.Type.ToString(),
            field.Required,
            field.Description,
            field.Group,
            field.Default,
            field.Placeholder,
            field.HelpText,
            field.Options.Select(option => new ConnectorFieldOptionView(option.Value, option.Label)).ToList(),
            field.VisibleWhen is null ? null : new ConnectorFieldVisibilityView(field.VisibleWhen.Field, field.VisibleWhen.EqualsValue));

        private static IReadOnlyList<ConnectorCategoryView> Categories(IConnector connector)
        {
            var categories = connector.Categories.Count == 0 ? new[] { ConnectorCategory.Uncategorized } : connector.Categories;
            return categories.Distinct().Select(ConnectorCategories.Info).OrderBy(info => info.Order).Select(info => new ConnectorCategoryView(info.Id, info.Label, info.Order)).ToList();
        }
    }
}
