using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.NTag424;
using BTCPayServer.Tests;
using LNURL;
using NBitcoin.DataEncoders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenQA.Selenium;
using OpenQA.Selenium.Support.Extensions;
using OpenQA.Selenium.Support.UI;
using System.Security.Cryptography;
using System.Text;
using Xunit.Abstractions;
using static System.Net.WebRequestMethods;

namespace BTCPayServer.Plugins.Boltcards.Tests;

[Trait("Plugin", "Plugin")]
public class BoltcardPluginTests : UnitTestBase
{
    public BoltcardPluginTests(ITestOutputHelper helper) : base(helper)
    {
        var testDir = new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", "..", "btcpayserver", "BTCPayServer.Tests", "bin", "Debug", "net8.0")).FullName;
        Directory.SetCurrentDirectory(testDir);
    }

    class BoltcardFactoryClient
    {
        public BoltcardFactoryClient(HttpClient client)
        {
            Client = client;
        }
        public HttpClient Client { get; }

        public async Task<RegisterBoltcardResponse> SetupBoltcard(string deeplink, byte[]? uid = null)
        {
            var lnurwl = Uri.UnescapeDataString(deeplink.Substring("boltcard://program?url=".Length));
            uid ??= RandomNumberGenerator.GetBytes(7);
            var resp = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, lnurwl)
            {
                Content = new StringContent("{\"UID\":\"" + Encoders.Hex.EncodeData(uid) + "\"}", Encoding.UTF8, "application/json")
            });
            resp.EnsureSuccessStatusCode();
            using (resp)
            {
                return JsonConvert.DeserializeObject<RegisterBoltcardResponse>(await resp.Content.ReadAsStringAsync())!;
            }
        }
        public async Task<LNURLPayRequest.LNURLPayRequestCallbackResponse> GetTopUp(string p, long msats)
        {
            using var resp = await Client.GetAsync("boltcard/top-up?amount=" + msats + "&p=" + p);
            resp.EnsureSuccessStatusCode();
            return JsonConvert.DeserializeObject<LNURLPayRequest.LNURLPayRequestCallbackResponse>(await resp.Content.ReadAsStringAsync())!;
        }
    }

    [Fact]
    public async Task CanTopUp()
    {
        using var s = this.CreateSeleniumTester();
        s.Server.ActivateLightning();
        await s.StartAsync();
        await s.Server.EnsureChannelsSetup();
        var userId = s.RegisterNewUser(true);
        var storeId = s.CreateNewStore().storeId;
        s.GenerateWallet();
        s.AddLightningNode();

        foreach (var input in new[]
        {
           (Currency: "SATS", Amount: "1000", TopUpAmount: 123456, ExpectedAmount: 123m),
           (Currency: "BTC", Amount: "1", TopUpAmount: 789000, ExpectedAmount: 0.00000789m)
        })
        {
            s.GoToHome();
            var appId = s.CreateApp("BoltcardFactory", "My Factory").appId;
            s.Driver.FindElement(By.Id("Amount")).Clear();
            s.Driver.FindElement(By.Id("Amount")).SendKeys(input.Amount);
            new SelectElement(s.Driver.FindElement(By.Id("Currency"))).SelectByText(input.Currency);
            s.Driver.FindElement(By.Id("Save")).Click();
            Assert.Contains("Pull payment request created", s.FindAlertMessage().Text);
            s.Driver.FindElement(By.Id("ViewApp")).Click();
            // Somehow stupid selenium doesn't like click on ViewApp, it just ignore it
            s.GoToUrl($"/apps/{appId}/boltcardfactory");
            // The QR to browse there should be available
            s.Driver.FindElement(By.Id("qr"));
            s.GoToUrl($"/apps/{appId}/boltcardfactory?isMobile=true");

            var setupDeepLink = s.Driver.FindElement(By.Id("setup-link")).GetAttribute("href");
            var resetDeepLink = s.Driver.FindElement(By.Id("reset-link")).GetAttribute("href");
            Assert.StartsWith("boltcard://program?url=", setupDeepLink);
            var client = new BoltcardFactoryClient(s.Server.PayTester.HttpClient);

            var uid = RandomNumberGenerator.GetBytes(7);
            var resp = await client.SetupBoltcard(setupDeepLink, uid);
            Assert.Equal(0, resp.Version);
            resp = await client.SetupBoltcard(setupDeepLink, uid);
            Assert.Equal(1, resp.Version);
            var enc = AESKey.Parse(resp.K1);
            var p = Encrypt(enc, new BoltcardPICCData(uid, 0));
            var topup = await client.GetTopUp(p, input.TopUpAmount);
            var paid = await s.Server.CustomerLightningD.Pay(topup.Pr);
            Assert.Equal(PayResult.Ok, paid.Result);

            var issuerKey = new IssuerKey(SettingsRepositoryExtensions.FixedKey());
            var db = s.Server.PayTester.GetService<ApplicationDbContextFactory>();
            var registration = await db.GetBoltcardRegistration(issuerKey, uid);
            Assert.NotNull(registration);

            var greenfield = new BTCPayServerClient(s.Server.PayTester.ServerUri, userId, s.Password);
            var payouts = await greenfield.GetPayouts(registration.PullPaymentId);
            var pp = await greenfield.GetPullPayment(registration.PullPaymentId);
            Assert.Equal(input.Currency, pp.Currency);

            var payout = Assert.Single(payouts);
            // The payment method amount is in sats, so msat are truncated
            Assert.Equal(-input.ExpectedAmount, payout.Amount);
            //Assert.Equal(-input.ExpectedPayoutMethodAmount, payout.PaymentMethodAmount);
        }
        //s.Driver.TakeScreenshot().SaveAsFile("C:\\Users\\NicolasDorier\\Downloads\\chromedriver-win64\\chromedriver-win64\\1.png");
    }

    private string Encrypt(AESKey enc, BoltcardPICCData data)
    {
        var piccData = new byte[] { 0xc7 }.Concat(data.Uid).Concat(NBitcoin.Utils.ToBytes((ulong)data.Counter, true)).ToArray();
        return Encoders.Hex.EncodeData(enc.Encrypt(piccData));
    }
}