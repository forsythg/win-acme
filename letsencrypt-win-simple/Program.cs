﻿using ACMESharp;
using Autofac;
using LetsEncrypt.ACME.Simple.Clients;
using LetsEncrypt.ACME.Simple.Extensions;
using LetsEncrypt.ACME.Simple.Plugins.StorePlugins;
using LetsEncrypt.ACME.Simple.Plugins.ValidationPlugins;
using LetsEncrypt.ACME.Simple.Plugins.ValidationPlugins.Http;
using LetsEncrypt.ACME.Simple.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Threading;

namespace LetsEncrypt.ACME.Simple
{
    class Program
    {
        private const string _clientName = "letsencrypt-win-simple";
        private static LetsEncryptClient _client;
        private static ISettingsService _settings;
        private static IInputService _input;
        private static CertificateService _certificateService;
        private static IStorePlugin _certificateStoreService;
        private static IStorePlugin _centralSslService;
        private static RenewalService _renewalService;
        private static IOptionsService _optionsService;
        private static Options _options;
        private static ILogService _log;
        public static PluginService Plugins;
        public static IContainer Container;

        static bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        static bool IsNET45 => Type.GetType("System.Reflection.ReflectionContext", false) != null;

        private static void RegisterServices(string[] args)
        {
            var builder = new ContainerBuilder();

            builder.
                RegisterInstance(new LogService()).
                As<ILogService>().
                SingleInstance();

            builder.RegisterType<OptionsService>().
                As<IOptionsService>().
                WithParameter(new TypedParameter(typeof(string[]), args)).
                SingleInstance();

            builder.RegisterType<SettingsService>().
                As<ISettingsService>().
                WithParameter(new TypedParameter(typeof(string), _clientName)).
                SingleInstance();

            builder.RegisterType<InputService>().
                As<IInputService>().
                SingleInstance();

            Container = builder.Build();
        }

        private static void Main(string[] args)
        {
            RegisterServices(args);

            // Basic services
            _log = Container.Resolve<ILogService>();
            _optionsService = Container.Resolve<IOptionsService>();
            _options = _optionsService.Options;
            if (_options == null) return;
            Plugins = new PluginService();
            _settings = Container.Resolve<ISettingsService>();
            _input = Container.Resolve<IInputService>();

            // .NET Framework check
            if (!IsNET45) {
                _log.Error(".NET Framework 4.5 or higher is required for this app");
                return;
            }

            // Show version information
            _input.ShowBanner();

            // Configure AcmeClient
            _client = new LetsEncryptClient(_input, _optionsService, _log, _settings);
            _certificateService = new CertificateService(_options, _log, _client, _settings.ConfigPath);
            _certificateStoreService = new CertificateStore(_options, _log);
            _centralSslService = new CentralSsl(_options, _log, _certificateService);
            _renewalService = new RenewalService(_settings, _input, _clientName, _settings.ConfigPath);
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;

            do {
                try {
                    if (_options.Renew)
                    {
                        CheckRenewals();
                        CloseDefault();
                    }
                    else if (!string.IsNullOrEmpty(_options.Plugin))
                    {
                        CreateNewCertificateUnattended();
                        CloseDefault();
                    }
                    else
                    {
                        MainMenu();
                    }
                } catch (AcmeClient.AcmeWebException awe) {
                    Environment.ExitCode = awe.HResult;
                    _log.Debug("AcmeWebException {@awe}", awe);
                    _log.Error(awe, "ACME Server Returned: {acmeWebExceptionMessage} - Response: {acmeWebExceptionResponse}", awe.Message, awe.Response.ContentAsString);
                } catch (AcmeException ae) {
                    Environment.ExitCode = ae.HResult;
                    _log.Debug("AcmeException {@ae}", ae);
                    _log.Error(ae, "AcmeException {@ae}", ae.Message);
                } catch (Exception e) {
                    Environment.ExitCode = e.HResult;
                    _log.Debug("Exception {@e}", e);
                    _log.Error(e, "Exception {@e}", e.Message);
                }
                if (!_options.CloseOnFinish)
                {
                    _options.Plugin = null;
                    _options.Renew = false;
                    _options.ForceRenewal = false;
                    Environment.ExitCode = 0;
                }
            } while (!_options.CloseOnFinish);
        }

        /// <summary>
        /// Present user with the option to close the program
        /// Useful to keep the console output visible when testing
        /// unattended commands
        /// </summary>
        private static void CloseDefault()
        {
            if (_options.Test && !_options.CloseOnFinish)
            {
                _options.CloseOnFinish = _input.PromptYesNo("Quit?");
            }
            else
            {
                _options.CloseOnFinish = true;
            }
        }

        /// <summary>
        /// Main user experience
        /// </summary>
        private static void MainMenu()
        {
            var options = new List<Choice<Action>>();
            options.Add(Choice.Create<Action>(() => CreateNewCertificate(), "Create new certificate", "N"));
            options.Add(Choice.Create<Action>(() => {
                var target = _input.ChooseFromList("Show details for renewal?",
                    _renewalService.Renewals,
                    x => Choice.Create(x),
                    true);
                if (target != null)
                {
                    try
                    {
                        _input.Show("Name", target.Binding.Host, true);
                        _input.Show("AlternativeNames", string.Join(", ", target.Binding.AlternativeNames));
                        _input.Show("ExcludeBindings", target.Binding.ExcludeBindings);
                        _input.Show("Target plugin", target.Binding.GetTargetPlugin().Description);
                        _input.Show("Validation plugin", target.Binding.GetValidationPlugin().Description);
                        _input.Show("Install plugin", target.Binding.Plugin.Description);
                        _input.Show("Renewal due", target.Date.ToUserString());
                        _input.Show("Script", target.Script);
                        _input.Show("ScriptParameters", target.ScriptParameters);
                        _input.Show("CentralSslStore", target.CentralSsl);
                        _input.Show("KeepExisting", target.KeepExisting);
                        _input.Show("Warmup", target.Warmup.ToString());
                        _input.Show("Renewed", $"{target.History.Count} times");
                        _input.WritePagedList(target.History.Select(x => Choice.Create(x)));
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Unable to list details for target");
                    }
                }
            }, "List scheduled renewals", "L"));

            options.Add(Choice.Create<Action>(() => {
                _options.Renew = true;
                CheckRenewals();
                _options.Renew = false;
            }, "Renew scheduled", "R"));

            options.Add(Choice.Create<Action>(() => {
                var target = _input.ChooseFromList("Which renewal would you like to run?",
                    _renewalService.Renewals,
                    x => Choice.Create(x),
                    true);
                if (target != null) {
                    _options.Renew = true;
                    _options.ForceRenewal = true;
                    ProcessRenewal(_renewalService.Renewals.ToList(), DateTime.Now, target);
                    _options.Renew = false;
                    _options.ForceRenewal = false;
                }
            }, "Renew specific", "S"));

            options.Add(Choice.Create<Action>(() => {
                _options.Renew = true;
                _options.ForceRenewal = true;
                CheckRenewals();
                _options.Renew = false;
                _options.ForceRenewal = false;
            }, "Renew *all*", "A"));

            options.Add(Choice.Create<Action>(() => {
                var target = _input.ChooseFromList("Which renewal would you like to cancel?",
                    _renewalService.Renewals, 
                    x => Choice.Create(x), 
                    true);

                if (target != null) {
                    if (_input.PromptYesNo($"Are you sure you want to delete {target}")) {
                        _renewalService.Renewals = _renewalService.Renewals.Except(new[] { target });
                        _log.Warning("Renewal {target} cancelled at user request", target);
                    }
                }
            }, "Cancel scheduled renewal", "C"));

            options.Add(Choice.Create<Action>(() => {
                ListRenewals();
                if (_input.PromptYesNo("Are you sure you want to delete all of these?")) {
                    _renewalService.Renewals = new List<ScheduledRenewal>();
                    _log.Warning("All scheduled renewals cancelled at user request");
                }
            }, "Cancel *all* scheduled renewals", "X"));

            options.Add(Choice.Create<Action>(() => {
                _options.CloseOnFinish = true;
                _options.Test = false;
            }, "Quit", "Q"));

            _input.ChooseFromList("Please choose from the menu", options, false).Invoke();
        }

        /// <summary>
        /// Create a new plug in unattended mode, triggered by the --plugin command line switch
        /// </summary>
        private static void CreateNewCertificateUnattended()
        {
            _log.Information(true, "Running in unattended mode.", _options.Plugin);

            var targetPlugin = Plugins.GetByName(Plugins.Target, _options.Plugin);
            if (targetPlugin == null)
            {
                _log.Error("Target plugin {name} not found.", _options.Plugin);
                return;
            }

            var target = targetPlugin.Default(_optionsService);
            if (target == null)
            {
                _log.Error("Plugin {name} was unable to generate a target", _options.Plugin);
                return;
            }
            else
            {
                _log.Information("Plugin {name} generated target {target}", _options.Plugin, target);
                target.TargetPluginName = targetPlugin.Name;
            }

            IValidationPlugin validationPlugin = null;
            if (!string.IsNullOrWhiteSpace(_options.Validation))
            {
                validationPlugin = Plugins.GetValidationPlugin($"{_options.ValidationMode}.{_options.Validation}");
                if (validationPlugin == null)
                {
                    _log.Error("Validation plugin {name} not found.", _options.Validation);
                    return;
                }
            }
            else
            {
                validationPlugin = Plugins.GetByName(Plugins.Validation, nameof(FileSystem));
            }
            target.ValidationPluginName = $"{validationPlugin.ChallengeType}.{validationPlugin.Name}";
            try
            {
                validationPlugin.Default(_optionsService, target);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Invalid validation input");
                return;
            }
            var result = Auto(target);
            if (!result.Success)
            {
                _log.Error("Create certificate failed: {message}", target, result.ErrorMessage);
            }
        }

        /// <summary>
        /// Print a list of scheduled renewals
        /// </summary>
        private static void ListRenewals()
        {
            _input.WritePagedList(_renewalService.Renewals.Select(x => Choice.Create(x)));
        }

        /// <summary>
        /// Interactive creation of new certificate
        /// </summary>
        private static void CreateNewCertificate()
        {
            // List options for generating new certificates
            var targetPlugin = _input.ChooseFromList(
                "Which kind of certificate would you like to create?", 
                Plugins.Target, 
                x => Choice.Create(x, description: x.Description),
                true);
            if (targetPlugin == null) return;

            var target = targetPlugin.Aquire(_optionsService, _input);
            if (target == null) {
                _log.Error("Plugin {Plugin} did not generate a target", targetPlugin.Name);
                return;
            } else {
                _log.Verbose("Plugin {Plugin} generated target {target}", targetPlugin.Name, target);
                target.TargetPluginName = targetPlugin.Name;
            }

            // Choose validation method
            var validationPlugin = _input.ChooseFromList(
                "How would you like to validate this certificate?",
                Plugins.Validation.Where(x => x.CanValidate(target)),
                x => Choice.Create(x, description: $"[{x.ChallengeType}] {x.Description}"), 
                false);

            target.ValidationPluginName = $"{validationPlugin.ChallengeType}.{validationPlugin.Name}";
            validationPlugin.Aquire(_optionsService, _input, target);
            var result = Auto(target);
            if (!result.Success)
            {
                _log.Error("Create certificate {target} failed: {message}", target, result.ErrorMessage);
            }
        }

        /// <summary>
        /// Get proxy server to use for web requests
        /// </summary>
        /// <returns></returns>
        public static IWebProxy GetWebProxy()
        {
            var system = "[System]";
            var useSystem = Properties.Settings.Default.Proxy.Equals(system, StringComparison.OrdinalIgnoreCase);

            var proxy = string.IsNullOrWhiteSpace(Properties.Settings.Default.Proxy) 
                ? null 
                : useSystem
                    ? WebRequest.GetSystemWebProxy() 
                    : new WebProxy(Properties.Settings.Default.Proxy);

            if (proxy != null)
            {
                Uri testUrl = new Uri("http://proxy.example.com");
                Uri proxyUrl = proxy.GetProxy(testUrl);
                bool useProxy = !string.Equals(testUrl.Host, proxyUrl.Host);
                if (useProxy)
                {
                    _log.Warning("Proxying via {proxy}", proxyUrl.Host);
                }
            } 
            return proxy;
         }
 
        public static RenewResult Auto(Target binding)
        {
            var targetPlugin = binding.GetTargetPlugin();
            foreach (var target in targetPlugin.Split(binding))
            {
                var auth = Authorize(target);
                if (auth.Status != "valid")
                {
                    return OnAutoFail(auth);
                }
            }
            return OnAutoSuccess(binding);
        }

        /// <summary>
        /// Steps to take on authorization failed
        /// </summary>
        /// <param name="auth"></param>
        /// <returns></returns>
        public static RenewResult OnAutoFail(AuthorizationState auth)
        {
            var errors = auth.Challenges.
                Select(c => c.ChallengePart).
                Where(cp => cp.Status == "invalid").
                SelectMany(cp => cp.Error);

            if (errors.Count() > 0)
            {
                _log.Error("ACME server reported:");
                foreach (var error in errors)
                {
                    _log.Error("[{_key}] {@value}", error.Key, error.Value);
                }
            }

            return new RenewResult(new AuthorizationFailedException(auth, errors.Select(x => x.Value)));
        }

        /// <summary>
        /// Steps to take on succesful (re)authorization
        /// </summary>
        /// <param name="binding"></param>
        public static RenewResult OnAutoSuccess(Target binding)
        {
            RenewResult result = new RenewResult(new Exception("Unknown error after validation"));
            try
            {
                var scheduled = _renewalService.Find(binding);
                var targetPlugin = binding.GetTargetPlugin();
                var oldCertificate = FindCertificate(scheduled);
                var newCertificate = _certificateService.RequestCertificate(binding);
                result = new RenewResult(newCertificate);

                if (_options.Test &&
                    !_options.Renew &&
                    !_input.PromptYesNo($"Do you want to install the certificate?"))
                    return result;

                SaveCertificate(newCertificate);

                if (_options.Renew ||
                    !_options.Test ||
                    _input.PromptYesNo($"Do you want to add/update the certificate to your server software?"))
                {
                    _log.Information("Installing SSL certificate in server software");
                    foreach (var target in targetPlugin.Split(binding))
                    {
                        if (_options.CentralSsl)
                        {
                            binding.Plugin.Install(binding);
                        }
                        else
                        {
                            binding.Plugin.Install(binding, newCertificate.PfxFile.FullName, newCertificate.Store, newCertificate.Certificate, oldCertificate.Certificate);
                        }
                    }

                    if (!_options.KeepExisting && oldCertificate != null)
                    {
                        DeleteCertificate(oldCertificate);
                    }
                }

                if (!_options.Renew &&
                    (scheduled != null || 
                    !_options.Test ||
                    _input.PromptYesNo($"Do you want to automatically renew this certificate in {_renewalService.RenewalPeriod} days? This will add a task scheduler task.")))
                {
                    _renewalService.CreateOrUpdate(binding, result);
                }
                return result;
            }
            catch (Exception ex)
            {
                // Result might still contain the Thumbprint of the certificate 
                // that was requested and (partially? installed, which might help
                // with debugging
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }
            return result;
        }

        /// <summary>
        /// Save certificate in the right place, either to the certifcate store 
        /// or to the central ssl store
        /// </summary>
        /// <param name="bindings">For which bindings is this certificate meant</param>
        /// <param name="certificate">The certificate itself</param>
        /// <param name="certificatePfx">The location of the PFX file in the local filesystem.</param>
        /// <param name="store">Certificate store to use when saving to one</param>
        public static void SaveCertificate(CertificateInfo certificateInfo)
        {
            var plugin = _options.CentralSsl ? _centralSslService : _certificateStoreService;
            plugin.Save(certificateInfo);
        }

        /// <summary>
        /// Remove certificate from Central SSL store or Certificate store
        /// </summary>
        /// <param name="thumbprint"></param>
        /// <param name="store"></param>
        public static void DeleteCertificate(CertificateInfo certificateInfo)
        {
            var plugin = _options.CentralSsl ? _centralSslService : _certificateStoreService;
            plugin.Delete(certificateInfo);
        }

        /// <summary>
        /// Find the most recently issued certificate for a specific target
        /// </summary>
        /// <param name="target"></param>
        /// <returns></returns>
        public static CertificateInfo FindCertificate(ScheduledRenewal scheduled)
        {
            if (scheduled == null)
            {
                return null;
            }
            var thumbprint = scheduled.History.
                OrderByDescending(x => x.Date).
                Where(x => x.Success).
                Select(x => x.Thumbprint).
                FirstOrDefault();
            var friendlyName = scheduled.Binding.Host;
            var useThumbprint = !string.IsNullOrEmpty(thumbprint);
            var storePlugin = _options.CentralSsl ? _centralSslService : _certificateStoreService;
            if (useThumbprint)
            {
                return storePlugin.FindByThumbprint(thumbprint);
            }
            else
            {
                return storePlugin.FindByFriendlyName(friendlyName);
            }
        }

        public static void CheckRenewals()
        {
            _log.Verbose("Checking renewals");

            var renewals = _renewalService.Renewals.ToList();
            if (renewals.Count == 0)
                _log.Warning("No scheduled renewals found.");

            var now = DateTime.UtcNow;
            foreach (var renewal in renewals)
                ProcessRenewal(renewals, now, renewal);
        }

        private static void ProcessRenewal(List<ScheduledRenewal> renewals, DateTime now, ScheduledRenewal renewal)
        {

            if (!_options.ForceRenewal)
            {
                _log.Verbose("Checking {renewal}", renewal.Binding.Host);
                if (renewal.Date >= now)
                {
                    _log.Information("Renewal for certificate {renewal} not scheduled, due after {date}", renewal.Binding.Host, renewal.Date.ToUserString());
                    return;
                }
            }

            _log.Information(true, "Renewing certificate for {renewal}", renewal.Binding.Host);
            _options.CentralSslStore = renewal.CentralSsl;
            _options.KeepExisting = string.Equals(renewal.KeepExisting, "true", StringComparison.InvariantCultureIgnoreCase);
            _options.Script = renewal.Script;
            _options.ScriptParameters = renewal.ScriptParameters;
            _options.Warmup = renewal.Warmup;
            try
            {
                // Let the plugin run
                var result = Auto(renewal.Binding);

                // Process result
                if (result.Success)
                {
                    renewal.Date = DateTime.UtcNow.AddDays(_renewalService.RenewalPeriod);
                    _log.Information(true, "Renewal for {host} succeeded, next one scheduled for {date}", renewal.Binding.Host, renewal.Date.ToUserString());
                }
                else
                {
                    _log.Error("Renewal for {host} failed, will retry on next run", renewal.Binding.Host);
                }

                // Store historical information
                if (renewal.History == null)
                {
                    renewal.History = new List<RenewResult>();
                }
                renewal.History.Add(result);

                // Persist to registry
                _renewalService.Renewals = renewals;
            }
            catch
            {
                _log.Error("Renewal for {host} failed, will retry on next run", renewal.Binding.Host);
            }
        }

        public static AuthorizationState Authorize(Target target)
        {
            List<string> identifiers = target.GetHosts(false);
            List<AuthorizationState> authStatus = new List<AuthorizationState>();
            foreach (var identifier in identifiers)
            {
                var authzState = _client.Acme.AuthorizeIdentifier(identifier);
                if (authzState.Status == "valid" && !_options.Test)
                {
                    _log.Information("Cached authorization result: {Status}", authzState.Status);
                    authStatus.Add(authzState);
                }
                else
                {
                    var validation = target.GetValidationPlugin();
                    if (validation == null)
                    {
                        return new AuthorizationState { Status = "invalid" };
                    }
                    _log.Information("Authorizing {dnsIdentifier} using {challengeType} validation ({name})", identifier, validation.ChallengeType, validation.Name);
                    var challenge = _client.Acme.DecodeChallenge(authzState, validation.ChallengeType);
                    var cleanUp = validation.PrepareChallenge(target, challenge, identifier, _options, _input);

                    try
                    {
                        _log.Debug("Submitting answer");
                        authzState.Challenges = new AuthorizeChallenge[] { challenge };
                        _client.Acme.SubmitChallengeAnswer(authzState, validation.ChallengeType, true);

                        // have to loop to wait for server to stop being pending.
                        // TODO: put timeout/retry limit in this loop
                        while (authzState.Status == "pending")
                        {
                            _log.Debug("Refreshing authorization");
                            Thread.Sleep(4000); // this has to be here to give ACME server a chance to think
                            var newAuthzState = _client.Acme.RefreshIdentifierAuthorization(authzState);
                            if (newAuthzState.Status != "pending")
                            {
                                authzState = newAuthzState;
                            }
                        }

                        _log.Information("Authorization result: {Status}", authzState.Status);
                        authStatus.Add(authzState);
                    }
                    finally
                    {
                        cleanUp(authzState);
                    }
                }
            }
            foreach (var authState in authStatus)
            {
                if (authState.Status != "valid")
                {
                    return authState;
                }
            }
            return new AuthorizationState { Status = "valid" };
        }

    }
}