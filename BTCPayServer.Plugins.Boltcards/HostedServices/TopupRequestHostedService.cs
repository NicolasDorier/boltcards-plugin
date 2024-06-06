using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using Microsoft.AspNet.SignalR.Client.Hubs;
using Microsoft.Extensions.Hosting;
using NBXplorer;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Boltcards.HostedServices
{
    internal class TopupRequestHostedService : IHostedService
    {
        public TopupRequestHostedService(
            PullPaymentHostedService paymentHostedService,
            EventAggregator eventAggregator)
        {
            PaymentHostedService = paymentHostedService;
            EventAggregator = eventAggregator;
        }
        private readonly CompositeDisposable _subscriptions = new CompositeDisposable();

        public PullPaymentHostedService PaymentHostedService { get; }
        public EventAggregator EventAggregator { get; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // This is a hack because 1.13.2 doesn't register to InvoiceEvent and thus never call TopUpInvoice
            // This class will not be needed in the future from 1.13.3
            var topupInvoice = typeof(PullPaymentHostedService).GetMethod("TopUpInvoice", BindingFlags.NonPublic | BindingFlags.Instance);
            if (topupInvoice is not null)
                _subscriptions.Add(EventAggregator.Subscribe<Events.InvoiceEvent>(evt => topupInvoice.Invoke(PaymentHostedService, [evt])));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _subscriptions.Dispose();
            return Task.CompletedTask;
        }
    }
}
