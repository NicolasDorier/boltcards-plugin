using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Controllers;
using BTCPayServer.Controllers.Greenfield;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Lightning;
using BTCPayServer.Models;
using BTCPayServer.NTag424;
using BTCPayServer.Plugins.BoltcardBalance.ViewModels;
using BTCPayServer.Plugins.BoltcardFactory;
using BTCPayServer.Services;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Stores;
using LNURL;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Internal;
using NBitcoin.Altcoins;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BoltcardBalance.Controllers
{
    [AutoValidateAntiforgeryToken]
    public class UIBoltcardBalanceController : Controller
    {
        private readonly ApplicationDbContextFactory _dbContextFactory;
        private readonly SettingsRepository _settingsRepository;
		private readonly StoreRepository storeRepository;
		private readonly BTCPayServerEnvironment _env;
		private readonly UILNURLController lnurlController;
		private readonly PullPaymentHostedService ppService;
		private readonly BTCPayNetworkJsonSerializerSettings _serializerSettings;

        public UIBoltcardBalanceController(
            ApplicationDbContextFactory dbContextFactory,
            SettingsRepository settingsRepository,
            StoreRepository storeRepository,
            BTCPayServerEnvironment env,
			UILNURLController lnurlController,
            PullPaymentHostedService ppService,
			BTCPayNetworkJsonSerializerSettings serializerSettings)
        {
            _dbContextFactory = dbContextFactory;
            _settingsRepository = settingsRepository;
			this.storeRepository = storeRepository;
			_env = env;
			this.lnurlController = lnurlController;
			this.ppService = ppService;
			_serializerSettings = serializerSettings;
        }
        [HttpGet("boltcards/balance")]
        public async Task<IActionResult> ScanCard([FromQuery] string p = null, [FromQuery] string c = null, [FromQuery]string view = null)
        {
            if (p is null || c is null)
            {
                return View($"{BoltcardBalancePlugin.ViewsDirectory}/ScanCard.cshtml");
            }

            //return View($"{BoltcardBalancePlugin.ViewsDirectory}/BalanceView.cshtml", new BalanceViewModel()
            //{
            //    AmountDue = 10000m,
            //    Currency = "SATS",
            //    Transactions = [new() { Date = DateTimeOffset.UtcNow, Balance = -3.0m }, new() { Date = DateTimeOffset.UtcNow, Balance = -5.0m }],
            //    ViewMode = Mode.Reset
            //});

            var issuerKey = await _settingsRepository.GetIssuerKey(_env);
            var boltData = issuerKey.TryDecrypt(p);
            if (boltData?.Uid is null)
                return NotFound();
            var id = issuerKey.GetId(boltData.Uid);
            var registration = await _dbContextFactory.GetBoltcardRegistration(issuerKey, boltData, true);
            if (registration is null)
                return NotFound();
            return await GetBalanceView(registration, p, issuerKey, view);
        }

		[AllowAnonymous]
		[HttpGet("~/boltcard/top-up")]
        [IgnoreAntiforgeryToken]
		public async Task<IActionResult> GetTopupBoltcardRequest([FromQuery] string p, [FromQuery] long? amount = null)
		{
			var issuerKey = await _settingsRepository.GetIssuerKey(_env);
			var piccData = issuerKey.TryDecrypt(p);
			if (piccData is null)
				return BadRequest(new LNUrlStatusResponse { Status = "ERROR", Reason = "Invalid PICCData" });
			piccData = new BoltcardPICCData(piccData.Uid, int.MaxValue - 10); // do not check the counter
			var registration = await _dbContextFactory.GetBoltcardRegistration(issuerKey, piccData, false);
			var pp = await ppService.GetPullPayment(registration!.PullPaymentId, false);
			var store = await storeRepository.FindStore(pp.StoreId);

			var lnUrlMetadata = new Dictionary<string, string>
			{
				{ "text/plain", "Boltcard Top-Up" }
			};
			var payRequest = new LNURLPayRequest
			{
				Tag = "payRequest",
				MinSendable = LightMoney.Satoshis(1.0m),
				MaxSendable = LightMoney.FromUnit(6.12m, LightMoneyUnit.BTC),
				Callback = new Uri(GetPayLink(p, Request.Scheme), UriKind.Absolute),
				CommentAllowed = 0
			};
			payRequest.Metadata = Newtonsoft.Json.JsonConvert.SerializeObject(lnUrlMetadata.Select(kv => new[] { kv.Key, kv.Value }));
			if (amount is null)
				return Ok(payRequest);

            var cryptoCode = "BTC";
			var currency = pp.GetBlob().Currency;
            if (currency != cryptoCode || currency is not ("SATS" or "BTC"))
				return BadRequest(new LNUrlStatusResponse { Status = "ERROR", Reason = $"Invalid currency for the pull payment (only {cryptoCode} and SATS are supported)" });

            var unit = currency == "SATS" ? LightMoneyUnit.Satoshi : LightMoneyUnit.BTC;
			var invoiceAmount = LightMoney.FromUnit(amount.Value, LightMoneyUnit.MilliSatoshi).ToUnit(unit);
			lnurlController.ControllerContext.HttpContext = HttpContext;
			var result = await lnurlController.GetLNURLRequest(
				   cryptoCode,
				   store,
				   store.GetStoreBlob(),
				   new CreateInvoiceRequest()
				   {
					   Currency = currency,
					   Amount = invoiceAmount
				   },
				   payRequest,
				   lnUrlMetadata,
				   [PullPaymentHostedService.GetInternalTag(pp.Id)]);
			if (result is not OkObjectResult ok || ok.Value is not LNURLPayRequest payRequest2)
				return result;
			payRequest = payRequest2;
			var invoiceId = payRequest.Callback.AbsoluteUri.Split('/').Last();
			return await lnurlController.GetLNURLForInvoice(invoiceId, cryptoCode, amount.Value, null);
		}

		private string GetPayLink(string p, string scheme)
		{
			return Url.Action(nameof(GetTopupBoltcardRequest), "UIBoltcard", new { p }, scheme)!;
		}

		[NonAction]
        public async Task<IActionResult> GetBalanceView(BoltcardDataExtensions.BoltcardRegistration registration, string p, IssuerKey issuerKey, string view = null)
        {
            var ppId = registration.PullPaymentId;
            var boltCardKeys = issuerKey.CreatePullPaymentCardKey(registration.UId, registration.Version, ppId).DeriveBoltcardKeys(issuerKey);
            await using var ctx = _dbContextFactory.CreateContext();
            var pp = await ctx.PullPayments.FindAsync(ppId);
            if (pp is null)
                return NotFound();
            var blob = pp.GetBlob();

            var payouts = (await ctx.Payouts.GetPayoutInPeriod(pp)
                    .OrderByDescending(o => o.Date)
                    .ToListAsync())
                    .Select(o => new
                    {
                        Entity = o,
                        Blob = o.GetBlob(_serializerSettings)
                    });


            var totalPaid = payouts.Where(p => p.Entity.State != PayoutState.Cancelled).Select(p => p.Blob.Amount).Sum();

            var bech32LNUrl = new Uri(Url.Action(nameof(GetTopupBoltcardRequest), "UIBoltcard", new { p }, Request.Scheme), UriKind.Absolute);
            bech32LNUrl = LNURL.LNURL.EncodeUri(bech32LNUrl, "payRequest", true);
            var vm = new BalanceViewModel()
            {
                Currency = blob.Currency,
                AmountDue = blob.Limit - totalPaid,
                LNUrlBech32 = bech32LNUrl.AbsoluteUri,
                LNUrlPay = Url.Action(nameof(GetTopupBoltcardRequest), "UIBoltcard", new { p }, "lnurlp"),
                BoltcardKeysResetLink = $"boltcard://reset?url={GetBoltcardDeeplinkUrl(pp.Id, OnExistingBehavior.KeepVersion)}",
                WipeData = JObject.FromObject(new
                {
                    version = 1,
                    action = "wipe",
                    k0 = Encoders.Hex.EncodeData(boltCardKeys.AppMasterKey.ToBytes()).ToUpperInvariant(),
                    k1 = Encoders.Hex.EncodeData(boltCardKeys.EncryptionKey.ToBytes()).ToUpperInvariant(),
                    k2 = Encoders.Hex.EncodeData(boltCardKeys.AuthenticationKey.ToBytes()).ToUpperInvariant(),
                    k3 = Encoders.Hex.EncodeData(boltCardKeys.K3.ToBytes()).ToUpperInvariant(),
                    k4 = Encoders.Hex.EncodeData(boltCardKeys.K4.ToBytes()).ToUpperInvariant(),
               
                }).ToString(Newtonsoft.Json.Formatting.None),
                PullPaymentLink = Url.Action(nameof(UIPullPaymentController.ViewPullPayment), "UIPullPayment", new { pullPaymentId = pp.Id }, Request.Scheme, Request.Host.ToString())
            };
            foreach (var payout in payouts)
            {
                vm.Transactions.Add(new BalanceViewModel.Transaction()
                {
                    Date = payout.Entity.Date,
                    Balance = -payout.Blob.Amount,
                    Status = payout.Entity.State
                });
            }
            vm.Transactions.Add(new BalanceViewModel.Transaction()
            {
                Date = pp.StartDate,
                Balance = blob.Limit,
                Status = PayoutState.Completed
            });
            vm.ViewMode = view?.Equals("Reset", StringComparison.OrdinalIgnoreCase) is true ? Mode.Reset : Mode.TopUp;
            return View($"{BoltcardBalancePlugin.ViewsDirectory}/BalanceView.cshtml", vm);
        }

        private string GetBoltcardDeeplinkUrl(string ppId, OnExistingBehavior onExisting)
        {
            var registerUrl = Url.Action(nameof(GreenfieldPullPaymentController.RegisterBoltcard), "GreenfieldPullPayment",
                            new
                            {
                                pullPaymentId = ppId,
                                onExisting = onExisting.ToString()
                            }, Request.Scheme, Request.Host.ToString());
            registerUrl = Uri.EscapeDataString(registerUrl);
            return registerUrl;
        }
    }
}
