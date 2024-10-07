using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Plugins.BoltcardFactory;
using BTCPayServer.Plugins.Boltcards;
using BTCPayServer.Plugins.Boltcards.HostedServices;
using BTCPayServer.Services.Apps;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.Boltcards
{
    public class BoltcardPlugin : BaseBTCPayServerPlugin
    {
		public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
		{
			new() { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }
		};
		public override void Execute(IServiceCollection services)
		{
			services.AddHostedService<TopupRequestHostedService>();
            services.AddUIExtension("header-nav", $"{BoltcardBalance.BoltcardBalancePlugin.ViewsDirectory}/NavExtension.cshtml");
			services.AddSingleton<AppBaseType, BoltcardFactory.BoltcardFactoryPlugin.BoltcardFactoryAppType>();
			services.AddUIExtension("header-nav", $"{BoltcardFactory.BoltcardFactoryPlugin.ViewsDirectory}/NavExtension.cshtml");
		}
	}
}
