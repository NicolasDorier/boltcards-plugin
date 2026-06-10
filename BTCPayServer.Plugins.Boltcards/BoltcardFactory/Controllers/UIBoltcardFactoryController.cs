#nullable enable
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Controllers;
using BTCPayServer.Data;
using BTCPayServer.Filters;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Extensions;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc.Rendering;
using BTCPayServer.Services.Apps;
using BTCPayServer.Services.Rates;
using System.Text.RegularExpressions;
using System;
using BTCPayServer.Services.Stores;
using BTCPayServer.Payments;
using BTCPayServer.Client.Models;
using System.Threading;
using BTCPayServer.Plugins.BoltcardFactory.ViewModels;
using BTCPayServer.Controllers.Greenfield;
using BTCPayServer.Payouts;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.Plugins.Wallets.Views.ViewModels;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.BoltcardFactory.Controllers
{
    [AutoValidateAntiforgeryToken]
    [Route("apps")]
    public class UIBoltcardFactoryController : Controller
    {
        private readonly PayoutMethodHandlerDictionary _payoutHandlers;
        private readonly AppService _appService;
        private readonly StoreRepository _storeRepository;
        private readonly CurrencyNameTable _currencyNameTable;
        private readonly IAuthorizationService _authorizationService;
        private readonly GreenfieldStoreAutomatedLightningPayoutProcessorsController _controller;

        public UIBoltcardFactoryController(
            PayoutMethodHandlerDictionary payoutHandlers,
            AppService appService,
            GreenfieldStoreAutomatedLightningPayoutProcessorsController controller,
            StoreRepository storeRepository,
            CurrencyNameTable currencyNameTable,
            IAuthorizationService authorizationService)
        {
            _payoutHandlers = payoutHandlers;
            _appService = appService;
            _controller = controller;
            _storeRepository = storeRepository;
            _currencyNameTable = currencyNameTable;
            _authorizationService = authorizationService;
        }
        public Data.StoreData CurrentStore => HttpContext.GetStoreData();
        private AppData GetCurrentApp() => HttpContext.GetAppData()!;
        [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [HttpGet("{appId}/settings/boltcardfactory")]
        public IActionResult UpdateBoltcardFactory(string appId)
        {
            var payoutMethods = _payoutHandlers.GetSupportedPayoutMethods(CurrentStore);
            if (!payoutMethods.Any())
            {
                TempData.SetStatusMessageModel(new StatusMessageModel
                {
                    Message = "You must enable at least one payment method before creating a pull payment.",
                    Severity = StatusMessageModel.StatusSeverity.Error
                });
                return RedirectToAction(nameof(UIStoresController.Dashboard), "UIStores", new { storeId = CurrentStore.Id });
            }

            var req = GetCurrentApp().GetSettings<CreatePullPaymentRequest>();
            return base.View($"{BoltcardFactoryPlugin.ViewsDirectory}/UpdateBoltcardFactory.cshtml", CreateViewModel(payoutMethods, req));
        }

        private NewPullPaymentModel CreateViewModel(IEnumerable<PayoutMethodId> payoutMethodIds, CreatePullPaymentRequest req)
        {
            return new NewPullPaymentModel
            {
                Name = GetCurrentApp().Name,
                Currency = req.Currency,
                Amount = req.Amount,
                AutoApproveClaims = req.AutoApproveClaims,
                Description = req.Description,
                PayoutMethods = req.PayoutMethods,
                BOLT11Expiration = req.BOLT11Expiration?.TotalDays is double v ? (long)v : 30,
                PayoutMethodsItem =
                                payoutMethodIds.Select(id => new SelectListItem(id.ToString(), id.ToString(), req.PayoutMethods?.Contains(id.ToString()) is not false))
            };
        }
        
        async Task<IEnumerable<LightningAutomatedPayoutSettings>>
            GetStoreLightningAutomatedPayoutProcessors(string storeId, string? paymentMethod = null,
                CancellationToken token = default)
        {
            return GetFromActionResult<IEnumerable<LightningAutomatedPayoutSettings>>(
                await _controller.GetStoreLightningAutomatedPayoutProcessors(storeId, paymentMethod)) ?? [];
        }
        async Task<LightningAutomatedPayoutSettings?> UpdateStoreLightningAutomatedPayoutProcessors(
            string storeId, string paymentMethod,
            LightningAutomatedPayoutSettings request, CancellationToken token = default)
        {
            return GetFromActionResult<LightningAutomatedPayoutSettings>(
                await _controller.UpdateStoreLightningAutomatedPayoutProcessor(storeId, paymentMethod, request));
        }
        private T? GetFromActionResult<T>(IActionResult result)
        {
            HandleActionResult(result);
            return result switch
            {
                JsonResult jsonResult when jsonResult.Value is T v=> v,
                OkObjectResult { Value: T res } => res,
                OkObjectResult { Value: JValue res } => res.Value<T>(),
                _ => default
            };
        }
        private void HandleActionResult(IActionResult result)
        {
            switch (result)
            {
                case UnprocessableEntityObjectResult { Value: List<GreenfieldValidationError> validationErrors }:
                    throw new GreenfieldValidationException(validationErrors.ToArray());
                case BadRequestObjectResult { Value: GreenfieldAPIError error }:
                    throw new GreenfieldAPIException(400, error);
                case ObjectResult { Value: GreenfieldAPIError error }:
                    throw new GreenfieldAPIException(400, error);
                case NotFoundResult _:
                    throw new GreenfieldAPIException(404, new GreenfieldAPIError("not-found", ""));
                default:
                    return;
            }
        }

        [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [HttpGet("{appId}/settings/boltcardfactory/setup")]
        public async Task<IActionResult> SetupLightningProcessor(string appId)
        {
            var pmi = PayoutMethodId.Parse("BTC-LN").ToString();
            var processors = await GetStoreLightningAutomatedPayoutProcessors(this.CurrentStore.Id, null);
            var processor = processors.FirstOrDefault(p => p.PayoutMethodId == pmi);
            if (processor is null)
                processor = new() { IntervalSeconds = TimeSpan.FromMinutes(AutomatedPayoutConstants.DefaultIntervalMinutes) };
            processor.ProcessNewPayoutsInstantly = true;
            await UpdateStoreLightningAutomatedPayoutProcessors(this.CurrentStore.Id, pmi, processor);
            this.TempData.SetStatusMessageModel(new StatusMessageModel()
            {
                Severity = StatusMessageModel.StatusSeverity.Success,
                Message = "The Lightning payout processor, with instant processing of approved payouts, has been set up."
            });
            return RedirectToAction(nameof(this.UpdateBoltcardFactory), new { appId });
        }

        [Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
        [HttpPost("{appId}/settings/boltcardfactory")]
        public async Task<IActionResult> UpdateBoltcardFactory(string appId, NewPullPaymentModel model)
        {
            if (CurrentStore is null)
                return NotFound();
            var storeId = CurrentStore.Id;
            var paymentMethodOptions = _payoutHandlers.GetSupportedPayoutMethods(CurrentStore);
            model.PayoutMethodsItem =
                paymentMethodOptions.Select(id => new SelectListItem(id.ToString(), id.ToString(), true));
            model.Name ??= string.Empty;
            model.Currency = model.Currency?.ToUpperInvariant()?.Trim() ?? String.Empty;
            model.PayoutMethods ??= new List<string>();

            if (!model.PayoutMethods.Any())
            {
                // Since we assign all payment methods to be selected by default above we need to update 
                // them here to reflect user's selection so that they can correct their mistake
                model.PayoutMethodsItem =
                    paymentMethodOptions.Select(id => new SelectListItem(id.ToString(), id.ToString(), false));
                ModelState.AddModelError(nameof(model.PayoutMethods), "You need at least one payment method");
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

            var selectedPaymentMethodIds = model.PayoutMethods.Select(PaymentMethodId.Parse).ToArray();
            if (!selectedPaymentMethodIds.All(id => selectedPaymentMethodIds.Contains(id)))
            {
                ModelState.AddModelError(nameof(model.Name), "Not all payment methods are supported");
            }
            if (!ModelState.IsValid)
                return View($"{BoltcardFactoryPlugin.ViewsDirectory}/UpdateBoltcardFactory.cshtml", model);

            model.AutoApproveClaims = true;
            
            var canApproveClaim = await CanApproveClaim();
            var previousSettings = GetCurrentApp().GetSettings<CreatePullPaymentRequest>();

            if (!canApproveClaim && !previousSettings.AutoApproveClaims && model.AutoApproveClaims)
            {
                ModelState.AddModelError(nameof(model.Name), "You need 'btcpay.store.cancreatepullpayments' permission.");
                return View($"{BoltcardFactoryPlugin.ViewsDirectory}/UpdateBoltcardFactory.cshtml", model);
            }

            var req = new CreatePullPaymentRequest()
            {
                Name = model.Name,
                Description = model.Description,
                Currency = model.Currency,
                Amount = model.Amount,
                AutoApproveClaims = model.AutoApproveClaims,
                BOLT11Expiration = TimeSpan.FromDays(model.BOLT11Expiration),
                PayoutMethods = model.PayoutMethods.ToArray()
            };
            var app = GetCurrentApp();
            app.Name = model.Name;
            app.SetSettings(req);
            await _appService.UpdateOrCreateApp(app);
            var payoutMethods = _payoutHandlers.GetSupportedPayoutMethods(CurrentStore);
            this.TempData.SetStatusMessageModel(new StatusMessageModel()
            {
                Message = "Pull payment request created",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
            return View($"{BoltcardFactoryPlugin.ViewsDirectory}/UpdateBoltcardFactory.cshtml", CreateViewModel(payoutMethods, req));
        }

        private async Task<bool> CanApproveClaim()
        {
            return (await
                _authorizationService.AuthorizeAsync(User, CurrentStore.Id, Policies.CanCreatePullPayments)).Succeeded;
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
