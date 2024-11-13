#nullable enable
using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Logging;
using BTCPayServer.NTag424;
using BTCPayServer.Services;
using BTCPayServer.Services.Apps;
using LNURL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NBitcoin.DataEncoders;

namespace BTCPayServer.Plugins.BoltcardFactory.Controllers
{
    [ApiController]
    [Route("apps")]
    public class APIBoltcardFactoryController : ControllerBase
    {
        private readonly AppService _appService;
        private readonly SettingsRepository _settingsRepository;
        private readonly BTCPayServerEnvironment _env;
        private readonly ApplicationDbContextFactory _dbContextFactory;
        private readonly PullPaymentHostedService _ppService;
        private readonly Logs logs;

        public APIBoltcardFactoryController(
            AppService appService,
            SettingsRepository settingsRepository,
            BTCPayServerEnvironment env,
            ApplicationDbContextFactory dbContextFactory,
            PullPaymentHostedService ppService,
            Logs logs)
        {
            _appService = appService;
            _settingsRepository = settingsRepository;
            _env = env;
            _dbContextFactory = dbContextFactory;
            _ppService = ppService;
            this.logs = logs;
        }
        [HttpPost("{appId}/boltcards")]
        [AllowAnonymous]
        public async Task<IActionResult> RegisterBoltcard(string appId, RegisterBoltcardRequest? request, string? onExisting = null)
        {
            var app = await _appService.GetApp(appId, BoltcardFactoryPlugin.AppType);
            if (app is null)
                return NotFound();
            var issuerKey = await _settingsRepository.GetIssuerKey(_env);
            logs.PayServer.LogInformation("IssuerKey: " + Encoders.Hex.EncodeData(issuerKey.AESKey.ToBytes()));
            BoltcardPICCData? picc = null;
            // LNURLW is used by deeplinks
            if (request?.LNURLW is not null)
            {
                if (request.UID is not null)
                {
                    ModelState.AddModelError(nameof(request.LNURLW), "You should pass either LNURLW or UID but not both");
                    return this.CreateValidationError(ModelState);
                }
                var p = ExtractP(request.LNURLW);
                if (p is null)
                {
                    ModelState.AddModelError(nameof(request.LNURLW), "The LNURLW should contains a 'p=' parameter");
                    return this.CreateValidationError(ModelState);
                }
                logs.PayServer.LogInformation("p: " + p);
                if (issuerKey.TryDecrypt(p) is not BoltcardPICCData o)
                {
                    ModelState.AddModelError(nameof(request.LNURLW), "The LNURLW 'p=' parameter cannot be decrypted");
                    return this.CreateValidationError(ModelState);
                }
                picc = o;
                request.UID = picc.Uid;
            }

            if (request?.UID is null || request.UID.Length != 7)
            {
                ModelState.AddModelError(nameof(request.UID), "The UID is required and should be 7 bytes");
                return this.CreateValidationError(ModelState);
            }

            // Passing onExisting as a query parameter is used by deeplink
            request.OnExisting = onExisting switch
            {
                nameof(OnExistingBehavior.UpdateVersion) => OnExistingBehavior.UpdateVersion,
                nameof(OnExistingBehavior.KeepVersion) => OnExistingBehavior.KeepVersion,
                _ => request.OnExisting
            };

            BoltcardKeys keys;
            int version;
            if (request.OnExisting == OnExistingBehavior.UpdateVersion)
            {
                var req = app.GetSettings<CreatePullPaymentRequest>();
                var ppId = await _ppService.CreatePullPayment(app.StoreDataId, req);
                version = await _dbContextFactory.LinkBoltcardToPullPayment(ppId, issuerKey, request.UID, request.OnExisting);
                keys = issuerKey.CreatePullPaymentCardKey(request.UID, version, ppId).DeriveBoltcardKeys(issuerKey);
            }
            // If it's a reset, do not create a new pull payment
            else
            {
                var registration = await _dbContextFactory.GetBoltcardRegistration(issuerKey, request.UID);
                if (registration?.PullPaymentId is null)
                {
                    ModelState.AddModelError(nameof(request.UID), "This card isn't registered");
                    return this.CreateValidationError(ModelState);
                }
                logs.PayServer.LogInformation("Registration Version: " + registration.Version);
                logs.PayServer.LogInformation("Registration Counter: " + registration.Counter);
                logs.PayServer.LogInformation("Registration Id: " + registration.Id);
                var ppId = registration.PullPaymentId;
                var pp = await _ppService.GetPullPayment(ppId, false);
                if (pp.StoreId != app.StoreDataId)
                {
                    ModelState.AddModelError(nameof(request.UID), "This card isn't registered");
                    return this.CreateValidationError(ModelState);
                }
                version = registration.Version;
                int retryCount = 0;
                var ppids = await GetPullPaymentIds(_dbContextFactory);
                retry:
                keys = issuerKey.CreatePullPaymentCardKey(request!.UID, version, ppId).DeriveBoltcardKeys(issuerKey);
                // The server version may be higher than the card.
                // If that is the case, let's try a few versions until we find the right one
                // by checking c.
                if (request?.LNURLW is { } lnurlw && 
                    ExtractC(lnurlw) is string c &&
                    picc is not null)
                {
                    if (!keys.AuthenticationKey.CheckSunMac(c, picc))
                    {
                        retryCount++;
                        ppId = ppids[retryCount];
                        if (version < 0 || retryCount >= ppids.Length)
                        {
                            ModelState.AddModelError(nameof(request.UID), "Unable to get keys of this card, it might be caused by a version mismatch");
                            return this.CreateValidationError(ModelState);
                        }
                        goto retry;
                    }
                }
            }

            var boltcardUrl = Url.Action(nameof(UIBoltcardController.GetWithdrawRequest), "UIBoltcard");
            boltcardUrl = Request.GetAbsoluteUri(boltcardUrl);
            boltcardUrl = Regex.Replace(boltcardUrl, "^https?://", "lnurlw://");

            var resp = new RegisterBoltcardResponse()
            {
                LNURLW = boltcardUrl,
                Version = version,
                K0 = Encoders.Hex.EncodeData(keys.AppMasterKey.ToBytes()).ToUpperInvariant(),
                K1 = Encoders.Hex.EncodeData(keys.EncryptionKey.ToBytes()).ToUpperInvariant(),
                K2 = Encoders.Hex.EncodeData(keys.AuthenticationKey.ToBytes()).ToUpperInvariant(),
                K3 = Encoders.Hex.EncodeData(keys.K3.ToBytes()).ToUpperInvariant(),
                K4 = Encoders.Hex.EncodeData(keys.K4.ToBytes()).ToUpperInvariant(),
            };

            return Ok(resp);
        }

        private async Task<string[]> GetPullPaymentIds(ApplicationDbContextFactory dbContextFactory)
        {
            using var ctx = dbContextFactory.CreateContext();
            return await ctx.PullPayments.OrderByDescending(p => p.StartDate).Select(p => p.Id).Take(60).ToArrayAsync();
        }

        private string? Extract(string? url, string param, int size)
        {
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return null;
            int num = uri.AbsoluteUri.IndexOf('?');
            if (num == -1)
                return null;
            string input = uri.AbsoluteUri.Substring(num);
            Match match = Regex.Match(input, param + "=([a-f0-9A-F]{"+ size +"})");
            if (!match.Success)
                return null;
            return match.Groups[1].Value;
        }
        private string? ExtractP(string? url) => Extract(url, "p", 32);
        private string? ExtractC(string? url) => Extract(url, "c", 16);
    }
}
