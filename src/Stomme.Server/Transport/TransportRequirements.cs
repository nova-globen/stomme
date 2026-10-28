using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Stomme.Models;

namespace Stomme.Transport
{
    /// <summary>
    /// The BL-53c enforcement: checks every module's declared <see cref="ModuleDefinition.Transports"/>
    /// requirement against the install's effective <see cref="TransportOptions"/> and logs an ERROR naming
    /// the module and the missing transport. Deliberately LOUD where <see cref="ModuleDefinition.Runtimes"/>
    /// enforcement is silent (the module just vanishes from pickers): a store-installed module whose
    /// endpoints never map is an operator-facing failure and must be attributable from the log alone.
    /// Called from <c>MapStommeTransport</c> — the one startup point where both the scanned declarations
    /// and the transport options are in hand — and it only ever logs; it never throws and never unmaps
    /// anything, so a misdeclared module cannot take the host down.
    /// </summary>
    public static class TransportRequirements
    {
        /// <summary>
        /// Logs one error per (module, declared-but-unsatisfied transport) pair. A declared transport name
        /// that is not a known transport at all is also an error — a typo'd declaration must not read as
        /// satisfied. Modules declaring nothing (the default) produce no output.
        /// </summary>
        public static void LogUnsatisfied(IEnumerable<ModuleDefinition> moduleDefinitions, TransportOptions options, ILogger logger)
        {
            ArgumentNullException.ThrowIfNull(moduleDefinitions);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(logger);

            foreach (var moduleDefinition in moduleDefinitions)
            {
                if (string.IsNullOrEmpty(moduleDefinition.Transports))
                {
                    continue;
                }

                foreach (var transport in moduleDefinition.Transports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (transport.Equals("Grpc", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!options.Grpc.Enabled)
                        {
                            LogMissing(logger, moduleDefinition, "Grpc");
                        }
                    }
                    else if (transport.Equals("SignalR", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!options.SignalR.Enabled)
                        {
                            LogMissing(logger, moduleDefinition, "SignalR");
                        }
                    }
                    else
                    {
                        logger.LogError(
                            "Module '{Module}' ({ModuleDefinitionName}) declares an unknown required transport '{Transport}'. " +
                            "Known transports are 'Grpc' and 'SignalR' (the flag names under the 'Transport' configuration section). " +
                            "The declaration cannot be checked and the module's endpoints may not be mapped.",
                            moduleDefinition.Name, moduleDefinition.ModuleDefinitionName, transport);
                    }
                }
            }
        }

        private static void LogMissing(ILogger logger, ModuleDefinition moduleDefinition, string transport)
        {
            logger.LogError(
                "Module '{Module}' ({ModuleDefinitionName}) requires the '{Transport}' transport, which is NOT enabled on this install — " +
                "the module's endpoints on that transport will not be mapped and the module will not work as declared. " +
                "Enable it via 'Transport:{TransportKey}:Enabled = true' in configuration, or remove the module.",
                moduleDefinition.Name, moduleDefinition.ModuleDefinitionName, transport, transport);
        }
    }
}
