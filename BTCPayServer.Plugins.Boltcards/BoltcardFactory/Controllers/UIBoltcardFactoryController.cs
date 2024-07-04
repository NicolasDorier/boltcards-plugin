#nullable enable
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Filters;
using BTCPayServer.Models.WalletViewModels;
using BTCPayServer.Plugins.PointOfSale;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Extensions;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.Rendering;
using BTCPayServer.Plugins.PointOfSale.Models;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Rates;
using System.Text.RegularExpressions;
using System;
using BTCPayServer.Services.Stores;
using BTCPayServer.Payments;
using Microsoft.AspNetCore.Identity;
using BTCPayServer.Client.Models;
using Org.BouncyCastle.Ocsp;
using BTCPayServer.NTag424;
using NBitcoin.DataEncoders;
using Newtonsoft.Json;
using BTCPayServer.Services;
using BTCPayServer.HostedServices;
using System.Threading;
using BTCPayServer.Plugins.BoltcardFactory.ViewModels;
using BTCPayServer.Controllers.Greenfield;
using BTCPayServer.Models;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.BoltcardFactory.Controllers
{
    [AutoValidateAntiforgeryToken]
    [Route("apps")]
    public class UIBoltcardFactoryController : Controller
    {
        private readonly IEnumerable<IPayoutHandler> _payoutHandlers;
        private readonly CurrencyNameTable _currencies;
        private readonly AppService _appService;
        private readonly StoreRepository _storeRepository;
        private readonly CurrencyNameTable _currencyNameTable;
        private readonly IAuthorizationService _authorizationService;

        public UIBoltcardFactoryController(
            IEnumerable<IPayoutHandler> payoutHandlers,
            CurrencyNameTable currencies,
            AppService appService,
            StoreRepository storeRepository,
            CurrencyNameTable currencyNameTable,
            IAuthorizationService authorizationService)
        {
            _payoutHandlers = payoutHandlers;
            _currencies = currencies;
            _appService = appService;
            _storeRepository = storeRepository;
            _currencyNameTable = currencyNameTable;
            _authorizationService = authorizationService;
        }
        public Data.StoreData CurrentStore => HttpContext.GetStoreData();
        private AppData GetCurrentApp() => HttpContext.GetAppData();
        [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [HttpGet("{appId}/settings/boltcardfactory")]
        public async Task<IActionResult> UpdateBoltcardFactory(string appId)
        {
            if (CurrentStore is null || GetCurrentApp() is null)
                return NotFound();

            var paymentMethods = await _payoutHandlers.GetSupportedPaymentMethods(CurrentStore);
            if (!paymentMethods.Any())
            {
                TempData.SetStatusMessageModel(new StatusMessageModel
                {
                    Message = "You must enable at least one payment method before creating a pull payment.",
                    Severity = StatusMessageModel.StatusSeverity.Error
                });
                return RedirectToAction(nameof(UIStoresController.Dashboard), "UIStores", new { storeId = CurrentStore.Id });
            }

            var req = GetCurrentApp().GetSettings<CreatePullPaymentRequest>();
            return base.View($"{BoltcardFactoryPlugin.ViewsDirectory}/UpdateBoltcardFactory.cshtml", CreateViewModel(paymentMethods, req));
        }

        private NewPullPaymentModel CreateViewModel(List<PaymentMethodId> paymentMethods, CreatePullPaymentRequest req)
        {
            return new NewPullPaymentModel
            {
                Name = GetCurrentApp().Name,
                Currency = req.Currency,
                Amount = req.Amount,
                AutoApproveClaims = req.AutoApproveClaims,
                Description = req.Description,
                PaymentMethods = req.PaymentMethods,
                BOLT11Expiration = req.BOLT11Expiration?.TotalDays is double v ? (long)v : 30,
                PaymentMethodItems =
                                paymentMethods.Select(id => new SelectListItem(id.ToPrettyString(), id.ToString(), true))
            };
        }

        [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [HttpPost("{appId}/settings/boltcardfactory")]
        public async Task<IActionResult> UpdateBoltcardFactory(string appId, NewPullPaymentModel model)
        {
            if (CurrentStore is null)
                return NotFound();
            var storeId = CurrentStore.Id;
            var paymentMethodOptions = await _payoutHandlers.GetSupportedPaymentMethods(CurrentStore);
            model.PaymentMethodItems =
                paymentMethodOptions.Select(id => new SelectListItem(id.ToPrettyString(), id.ToString(), true));
            model.Name ??= string.Empty;
            model.Currency = model.Currency?.ToUpperInvariant()?.Trim() ?? String.Empty;
            model.PaymentMethods ??= new List<string>();

            if (!model.PaymentMethods.Any())
            {
                // Since we assign all payment methods to be selected by default above we need to update 
                // them here to reflect user's selection so that they can correct their mistake
                model.PaymentMethodItems =
                    paymentMethodOptions.Select(id => new SelectListItem(id.ToPrettyString(), id.ToString(), false));
                ModelState.AddModelError(nameof(model.PaymentMethods), "You need at least one payment method");
            }
            if (_currencyNameTable.GetCurrencyData(model.Currency, false) is null)
            {
                ModelState.AddModelError(nameof(model.Currency), "Invalid currency");
            }
            if (model.Amount <= 0.0m)
            {
                ModelState.AddModelError(nameof(model.Amount), "The amount should be more than zero");
            }
            if (model.Name.Length > 50)
            {
                ModelState.AddModelError(nameof(model.Name), "The name should be maximum 50 characters.");
            }

            var selectedPaymentMethodIds = model.PaymentMethods.Select(PaymentMethodId.Parse).ToArray();
            if (!selectedPaymentMethodIds.All(id => selectedPaymentMethodIds.Contains(id)))
            {
                ModelState.AddModelError(nameof(model.Name), "Not all payment methods are supported");
            }
            if (!ModelState.IsValid)
                return View($"{BoltcardFactoryPlugin.ViewsDirectory}/UpdateBoltcardFactory.cshtml", model);
            model.AutoApproveClaims = model.AutoApproveClaims && (await
                _authorizationService.AuthorizeAsync(User, CurrentStore.Id, Policies.CanCreatePullPayments)).Succeeded;

            var req = new CreatePullPaymentRequest()
            {
                Name = model.Name,
                Description = model.Description,
                Currency = model.Currency,
                Amount = model.Amount,
                AutoApproveClaims = model.AutoApproveClaims,
                BOLT11Expiration = TimeSpan.FromDays(model.BOLT11Expiration),
                PaymentMethods = model.PaymentMethods.ToArray()
            };
            var app = GetCurrentApp();
            app.Name = model.Name;
            app.SetSettings(req);
            await _appService.UpdateOrCreateApp(app);
            var paymentMethods = await _payoutHandlers.GetSupportedPaymentMethods(CurrentStore);
            this.TempData.SetStatusMessageModel(new StatusMessageModel()
            {
                Message = "Pull payment request created",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
            return View($"{BoltcardFactoryPlugin.ViewsDirectory}/UpdateBoltcardFactory.cshtml", CreateViewModel(paymentMethods, req));
        }
        private async Task<string?> GetStoreDefaultCurrentIfEmpty(string storeId, string? currency)
        {
            if (string.IsNullOrWhiteSpace(currency))
            {
                currency = (await _storeRepository.FindStore(storeId))?.GetStoreBlob()?.DefaultCurrency;
            }
            return currency?.Trim().ToUpperInvariant();
        }
        private int[] ListSplit(string list, string separator = ",")
        {
            if (string.IsNullOrEmpty(list))
            {
                return Array.Empty<int>();
            }

            // Remove all characters except numeric and comma
            Regex charsToDestroy = new Regex(@"[^\d|\" + separator + "]");
            list = charsToDestroy.Replace(list, "");

            return list.Split(separator, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        }
        [HttpGet("/apps/{appId}/boltcardfactory")]
        [DomainMappingConstraint(BoltcardFactoryPlugin.AppType)]
        [AllowAnonymous]
        public IActionResult ViewBoltcardFactory(string appId)
        {
            var vm = new ViewBoltcardFactoryViewModel();
            vm.SetupDeepLink = $"boltcard://program?url={GetBoltcardDeeplinkUrl(appId, OnExistingBehavior.UpdateVersion)}";
            vm.ResetDeepLink = $"boltcard://reset?url={GetBoltcardDeeplinkUrl(appId, OnExistingBehavior.KeepVersion)}";
            return View($"{BoltcardFactoryPlugin.ViewsDirectory}/ViewBoltcardFactory.cshtml", vm);
        }

        private string GetBoltcardDeeplinkUrl(string appId, OnExistingBehavior onExisting)
        {
            var registerUrl = Url.Action(nameof(APIBoltcardFactoryController.RegisterBoltcard), "APIBoltcardFactory",
                            new
                            {
                                appId = appId,
                                onExisting = onExisting.ToString()
                            }, Request.Scheme, Request.Host.ToString());
            registerUrl = Uri.EscapeDataString(registerUrl!);
            return registerUrl;
        }   
    }
}
