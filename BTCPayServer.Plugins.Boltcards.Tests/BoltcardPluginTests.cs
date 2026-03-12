using BTCPayServer.Client;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.NTag424;
using BTCPayServer.Tests;
using LNURL;
using NBitcoin.DataEncoders;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Playwright;
using Xunit.Abstractions;
using static BTCPayServer.BoltcardDataExtensions;

namespace BTCPayServer.Plugins.Boltcards.Tests;

[Trait("Plugin", "Plugin")]
public class BoltcardPluginTests : UnitTestBase
{
    public BoltcardPluginTests(ITestOutputHelper helper) : base(helper)
    {
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
            var endpoint = Uri.UnescapeDataString(deeplink.Substring("boltcard://program?url=".Length));
            uid ??= RandomNumberGenerator.GetBytes(7);
            var resp = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, endpoint)
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

        public async Task<RegisterBoltcardResponse> ResetBoltcard(string resetDeepLink, string lnurl, BoltcardPICCData picc, IssuerKey issuerKey, CardKey key)
        {
            var endpoint = Uri.UnescapeDataString(resetDeepLink.Substring("boltcard://reset?url=".Length));
            var p = Encrypt(key.DeriveBoltcardKeys(issuerKey).EncryptionKey, picc);
            var c = Encoders.Hex.EncodeData(key.DeriveAuthenticationKey().GetSunMac(picc));
            lnurl += $"?p={p}&c={c}";
            var resp = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent("{\"LNURLW\":\"" + lnurl + "\"}", Encoding.UTF8, "application/json")
            });
            resp.EnsureSuccessStatusCode();
            using (resp)
            {
                return JsonConvert.DeserializeObject<RegisterBoltcardResponse>(await resp.Content.ReadAsStringAsync())!;
            }
        }
    }

    [Fact]
    public async Task CanTopUp()
    {
        await using var s = this.CreatePlaywrightTester();
        s.Server.ActivateLightning();
        await s.StartAsync();
        await s.Server.EnsureChannelsSetup();
        var userId = await s.RegisterNewUser(true);
        var storeId = (await s.CreateNewStore()).storeId;
        await s.GenerateWallet();
        await s.AddLightningNode();

        foreach (var input in new[]
        {
           (Currency: "SATS", Amount: "1000", TopUpAmount: 123456, ExpectedAmount: 123m, ExpectedPayoutAmount: 123m),
           (Currency: "BTC", Amount: "1", TopUpAmount: 789000, ExpectedAmount: 0.00000789m, ExpectedPayoutAmount: 0.00000789m)
        })
        {
            TestLogs.LogInformation($"Let's create a factory of {input.Amount} {input.Currency}");
            {
                await s.GoToHome();
                var appId = (await s.CreateApp("BoltcardFactory", "My Factory")).appId;
                await s.Page.FillAsync("#Amount", input.Amount);
                await s.Page.Locator("#Currency").SelectOptionAsync(new SelectOptionValue { Label = input.Currency });
                await s.Page.ClickAsync("#Save");
                await s.FindAlertMessage(partialText: "Pull payment request created");

                var source = await s.Page.ContentAsync();
                if (source.Contains("#SetupLightningProcessor"))
                {
                    await s.Page.ClickAsync("#SetupLightningProcessor");
                    await s.Page.ClickAsync("#Save");
                }
                await s.Page.ClickAsync("#ViewApp");
                // Somehow stupid selenium doesn't like click on ViewApp, it just ignore it
                await s.GoToUrl($"/apps/{appId}/boltcardfactory");
                // The QR to browse there should be available
                await s.Page.WaitForSelectorAsync("#qr");
                await s.GoToUrl($"/apps/{appId}/boltcardfactory?isMobile=true");
            }

            var setupDeepLink = await s.Page.GetAttributeAsync("#setup-link", "href");
            var resetDeepLink = await s.Page.GetAttributeAsync("#reset-link", "href");
            Assert.StartsWith("boltcard://program?url=", setupDeepLink);
            var client = new BoltcardFactoryClient(s.Server.PayTester.HttpClient);

            RegisterBoltcardResponse resp;
            byte[] uid = RandomNumberGenerator.GetBytes(7);
            TestLogs.LogInformation($"Let's setup a new boltcard (will be version 0)");
            {
                resp = await client.SetupBoltcard(setupDeepLink, uid);
                Assert.Equal(0, resp.Version);
            }

            TestLogs.LogInformation($"Let's re-setup the same boltcard (will be version 1)");
            {
                resp = await client.SetupBoltcard(setupDeepLink, uid);
                Assert.Equal(1, resp.Version);
            }

            BoltcardRegistration registration;
            var picc = new BoltcardPICCData(uid, 0);
            var enc = AESKey.Parse(resp.K1);
            var p = Encrypt(enc, picc);

            TestLogs.LogInformation($"If a re-setup failed, we would end up " +
                $"with a boltcard having a version lower than the version expected by the server. " +
                "Let's test if the server is smart enough to try older version.");
            {
                
                var db = s.Server.PayTester.GetService<ApplicationDbContextFactory>()!;
                var issuerKey = new IssuerKey(SettingsRepositoryExtensions.FixedKey());
                registration = (await db.GetBoltcardRegistration(issuerKey, uid))!;
                Assert.NotNull(registration);
                var oldCardKey = issuerKey.CreatePullPaymentCardKey(uid, 0, registration.PullPaymentId);
                resp = await client.ResetBoltcard(resetDeepLink, resp.LNURLW, picc, issuerKey, oldCardKey);
                Assert.Equal(AESKey.Parse(resp.K2), oldCardKey.DeriveAuthenticationKey());
            }

            TestLogs.LogInformation("Let's check that we can properly top-up");
            {
                var topup = await client.GetTopUp(p, input.TopUpAmount);
                var paid = await s.Server.CustomerLightningD.Pay(topup.Pr);
                Assert.Equal(PayResult.Ok, paid.Result);
                await Task.Delay(1000);

                var greenfield = new BTCPayServerClient(s.Server.PayTester.ServerUri, userId, s.Password);
                var payouts = await greenfield.GetPayouts(registration.PullPaymentId);
                var pp = await greenfield.GetPullPayment(registration.PullPaymentId);
                Assert.Equal(input.Currency, pp.Currency);

                var payout = Assert.Single(payouts);
                // The payment method amount is in sats, so msat are truncated
                Assert.Equal(-input.ExpectedAmount, payout.OriginalAmount);

                // The PaymentMethodAmount is null even outside topups, probably a bug in btcpay
                Assert.Equal(-input.ExpectedPayoutAmount, payout.PayoutAmount);
            }
        }
        //s.Driver.TakeScreenshot().SaveAsFile("C:\\Users\\NicolasDorier\\Downloads\\chromedriver-win64\\chromedriver-win64\\1.png");
    }

    internal static string Encrypt(AESKey enc, BoltcardPICCData data)
    {
        var piccData = ToBytes(data);
        return Encoders.Hex.EncodeData(enc.Encrypt(piccData));
    }

    internal static byte[] ToBytes(BoltcardPICCData data)
    {
        return new byte[] { 0xc7 }.Concat(data.Uid).Concat(NBitcoin.Utils.ToBytes((ulong)data.Counter, true)).ToArray();
    }
}