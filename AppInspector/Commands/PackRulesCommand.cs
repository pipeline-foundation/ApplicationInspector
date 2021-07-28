﻿// Copyright (C) Microsoft. All rights reserved.
// Licensed under the MIT License. See LICENSE.txt in the project root for license information.

using Microsoft.ApplicationInspector.RulesEngine;
using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ApplicationInspector.Common;

namespace Microsoft.ApplicationInspector.Commands
{
    public class PackRulesOptions : LogOptions
    {
        public bool RepackDefaultRules { get; set; }
        public string? CustomRulesPath { get; set; }
        public bool NotIndented { get; set; }
        public bool PackEmbeddedRules { get; set; }
    }

    public class PackRulesResult : Result
    {
        public enum ExitCode
        {
            Success = 0,
            Error = 1,
            CriticalError = Common.Utils.ExitCode.CriticalError //ensure common value for final exit log mention
        }

        [JsonProperty(Order = 2)]
        public ExitCode ResultCode { get; set; }

        /// <summary>
        /// List of Rules to pack as specified in pack command
        /// </summary>
        [JsonProperty(Order = 3)]
        public List<Rule>? Rules { get; set; }
    }

    /// <summary>
    /// Used to combine validated rules into one json for ease in distribution of this
    /// application
    /// </summary>
    public class PackRulesCommand
    {
        private readonly PackRulesOptions _options;
        private string? _rules_path;

        public PackRulesCommand(PackRulesOptions opt)
        {
            _options = opt;

            try
            {
                _options.Log ??= Common.Utils.SetupLogging(_options);
                WriteOnce.Log ??= _options.Log;

                ConfigureConsoleOutput();
                ConfigRules();
            }
            catch (OpException e)
            {
                WriteOnce.Error(e.Message);
                throw;
            }
        }

        #region configure

        /// <summary>
        /// Establish console verbosity
        /// For NuGet DLL use, console is muted overriding any arguments sent
        /// </summary>
        private void ConfigureConsoleOutput()
        {
            WriteOnce.SafeLog("PackRulesCommand::ConfigureConsoleOutput", LogLevel.Trace);

            //Set console verbosity based on run context (none for DLL use) and caller arguments
            if (!Common.Utils.CLIExecutionContext)
            {
                WriteOnce.Verbosity = WriteOnce.ConsoleVerbosity.None;
            }
            else
            {
                WriteOnce.ConsoleVerbosity verbosity = WriteOnce.ConsoleVerbosity.Medium;
                if (!Enum.TryParse(_options.ConsoleVerbosityLevel, true, out verbosity))
                {
                    throw new OpException(MsgHelp.FormatString(MsgHelp.ID.CMD_INVALID_ARG_VALUE, "-x"));
                }
                else
                {
                    WriteOnce.Verbosity = verbosity;
                }
            }
        }

        private void ConfigRules()
        {
            WriteOnce.SafeLog("PackRulesCommand::ConfigRules", LogLevel.Trace);

            if (_options.RepackDefaultRules && !Common.Utils.CLIExecutionContext)
            {
                throw new OpException(MsgHelp.GetString(MsgHelp.ID.VERIFY_RULES_NO_CLI_DEFAULT));
            }
            else if (!_options.RepackDefaultRules && string.IsNullOrEmpty(_options.CustomRulesPath) && !_options.PackEmbeddedRules)
            {
                throw new OpException(MsgHelp.GetString(MsgHelp.ID.CMD_NORULES_SPECIFIED));
            }
            else if (_options.RepackDefaultRules && !Directory.Exists(Common.Utils.GetPath(Common.Utils.AppPath.defaultRulesSrc)))
            {
                throw new OpException(MsgHelp.GetString(MsgHelp.ID.PACK_RULES_NO_DEFAULT));
            }

            _rules_path = _options.RepackDefaultRules ? Common.Utils.GetPath(Common.Utils.AppPath.defaultRulesSrc) : _options.CustomRulesPath;
        }

        #endregion configure

        /// <summary>
        /// Intentional as no identified value in calling from DLL at this time
        /// </summary>
        /// <returns></returns>
        public PackRulesResult GetResult()
        {
            WriteOnce.SafeLog("PackRules::Run", LogLevel.Trace);
            WriteOnce.Operation(MsgHelp.FormatString(MsgHelp.ID.CMD_RUNNING, "Pack Rules"));

            PackRulesResult packRulesResult = new PackRulesResult()
            {
                AppVersion = Common.Utils.GetVersionString()
            };

            try
            {
                RulesVerifier verifier = new RulesVerifier(_rules_path, _options?.Log);
                if (_options?.PackEmbeddedRules ?? false)
                {
                    verifier.LoadRuleSet(RuleSetUtils.GetDefaultRuleSet());
                }
                verifier.Verify();
                if (!verifier.IsVerified)
                {
                    throw new OpException(MsgHelp.GetString(MsgHelp.ID.VERIFY_RULES_RESULTS_FAIL));
                }
                packRulesResult.Rules = verifier.CompiledRuleset?.GetAppInspectorRules().ToList() ?? new List<Rule>();
                packRulesResult.ResultCode = PackRulesResult.ExitCode.Success;
            }
            catch (OpException e)
            {
                WriteOnce.Error(e.Message);
                //caught for CLI callers with final exit msg about checking log or throws for DLL callers
                throw;
            }

            return packRulesResult;
        }
    }
}