using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Plugins.BoltcardFactory;
using BTCPayServer.Plugins.Boltcards;
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
			new() { Identifier = nameof(BTCPayServer), Condition = ">=1.13.2" }
		};
		public override void Execute(IServiceCollection services)
		{
			services.AddSingleton<IUIExtension>(new UIExtension($"{BoltcardBalance.BoltcardBalancePlugin.ViewsDirectory}/NavExtension.cshtml", "header-nav"));
			services.AddSingleton<AppBaseType, BoltcardFactory.BoltcardFactoryPlugin.BoltcardFactoryAppType>();
			services.AddSingleton<IUIExtension>(new UIExtension($"{BoltcardFactory.BoltcardFactoryPlugin.ViewsDirectory}/NavExtension.cshtml", "header-nav"));
		}
	}
}
