using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using MyBox;
using UnityEngine;

namespace FastCheckout
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private static bool _patched;
        private static readonly Harmony Harmony = new(PluginInfo.PLUGIN_GUID);
        private static ConfigEntry<bool> _fastCheckout;
        private static ConfigEntry<KeyCode> _fastCheckoutKey;
        private static ConfigEntry<bool> _minimumChangeOnly;

        private void Awake()
        {
            _fastCheckout = Config.Bind("Logic", "Fast Checkout", false,
                "By enabling this option, all your checkouts will be instant. With a single click, you'll scan all items, receive payment, and finalize the transaction");
            _fastCheckoutKey = Config.Bind("Keys", "Fast Checkout Key", KeyCode.LeftShift,
                "With this key, you'll only perform a fast checkout while it's pressed. To use it, hold down this key while clicking to scan an item, and the entire process of scanning the remaining items, receiving payment, and clearing the customer will occur. Note that if the Fast Checkout option is activated, pressing this key will have no effect");

            _minimumChangeOnly = Config.Bind("Logic", "Minimum Change Only", true,
                "This means that when a customer pays in cash and you need to give change, you will return the minimum amount the game allows. Explanation: The game allows you to give the incorrect amount of change, enabling you to profit from customers who pay in cash");

            Logger.LogInfo($"Plugin loaded");
        }

        private void OnDestroy()
        {
            if (!_patched) return;
            _patched = false;
            Harmony?.UnpatchSelf();
            Logger.LogInfo($"Plugin unloaded");
        }

        private void Update()
        {
            if (Singleton<OnboardingManager>.Instance == null) return;
            if (Singleton<OnboardingManager>.Instance.Completed && !_patched)
            {
                _patched = true;
                Harmony.PatchAll();
                Logger.LogInfo("Patches applied");
            }
            else if (!Singleton<OnboardingManager>.Instance.Completed && _patched)
            {
                OnDestroy();
            }
        }

        [HarmonyPatch(typeof(CheckoutInteraction), "InteractWithProduct")]
        public static class CheckoutInteraction_InteractWithProduct
        {
            private const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance |
                                               BindingFlags.DeclaredOnly;

            private static readonly FieldInfo f_m_Customers = typeof(Checkout).GetField("m_Customers", flags);

            private static readonly FieldInfo f_m_PaymentMoney =
                typeof(Customer).GetField("m_PaymentMoney", flags);

            private static readonly FieldInfo
                m_PaymentCard_field = typeof(Customer).GetField("m_PaymentCard", flags);

            private static readonly FieldInfo f_m_CurrentMoney =
                typeof(CheckoutInteraction).GetField("m_CurrentMoney", flags);

            private static readonly MethodInfo m_InteractWithCustomerPayment =
                typeof(CheckoutInteraction).GetMethod("InteractWithCustomerPayment", flags);

            private static readonly FieldInfo f_m_CollectedChange =
                typeof(Checkout).GetField("m_CollectedChange", flags);

            private static readonly FieldInfo f_m_CorrectChange =
                typeof(Checkout).GetField("m_CorrectChange", flags);

            private static readonly FieldInfo f_m_CheckoutDrawer =
                typeof(Checkout).GetField("m_CheckoutDrawer", flags);

            private static readonly FieldInfo
                f_m_MoneySlots = typeof(CheckoutDrawer).GetField("m_MoneySlots", flags);

            private static readonly FieldInfo
                f_m_PosTerminal = typeof(Checkout).GetField("m_PosTerminal", flags);

            private static readonly MethodInfo m_OnApproveCheckout =
                typeof(CheckoutInteraction).GetMethod("OnApproveCheckout", flags);

            private static readonly FieldInfo f_m_Total = typeof(PosTerminal).GetField("m_Total", flags);

            private static readonly PropertyInfo p_m_CanApproveChange =
                typeof(Checkout).GetProperty("m_CanApproveChange", flags);

            private static readonly ManualLogSource
                _logger = BepInEx.Logging.Logger.CreateLogSource(PluginInfo.PLUGIN_NAME);

            public static bool Prefix(CheckoutInteraction __instance, Checkout ___m_Checkout)
            {
                if (!_fastCheckout.Value && !Input.GetKey(_fastCheckoutKey.Value)) return true;

                while (___m_Checkout.Belt.Products.Count > 0)
                {
                    ___m_Checkout.Belt.Products.First().Scan();
                }

                var customers = (List<Customer>)f_m_Customers.GetValue(___m_Checkout);

                var currentCustomer = customers.First();

                var cash = true;
                var gameObject = (GameObject)f_m_PaymentMoney.GetValue(currentCustomer);

                if (gameObject == null)
                {
                    gameObject = (GameObject)m_PaymentCard_field.GetValue(currentCustomer);
                    cash = false;
                }

                var currentMoney = gameObject.GetComponent<MoneyPack>();
                f_m_CurrentMoney.SetValue(__instance, currentMoney);

                m_InteractWithCustomerPayment.Invoke(__instance, null);

                if (cash)
                {
                    var correctChange = (float)f_m_CorrectChange.GetValue(___m_Checkout);
                    var minimumChange = Math.Round(Math.Max(
                        correctChange * 0.5,
                        correctChange - ___m_Checkout.TotalPrice * 0.5
                    ), 2);
                    
                    var collectedChange = 0f;
                    if (minimumChange > 0)
                    {
                        var checkoutDrawer = (CheckoutDrawer)f_m_CheckoutDrawer.GetValue(___m_Checkout);
                        var moneySlots = (MoneyPack[])f_m_MoneySlots.GetValue(checkoutDrawer);
                        moneySlots = moneySlots.OrderByDescending(mp => mp.Value).ToArray();

                        bool canApproveChange;
                        do
                        {
                            var betterMoneyPack = moneySlots.FirstOrDefault(mp =>
                                Math.Round(mp.Value + collectedChange, 2) <= (_minimumChangeOnly.Value ? minimumChange : correctChange));
                            if (betterMoneyPack == null)
                            {
                                betterMoneyPack = moneySlots.Last();
                            }

                            ___m_Checkout.AddOrRemoveChange(betterMoneyPack, true);
                            collectedChange = (float)f_m_CollectedChange.GetValue(___m_Checkout);
                            canApproveChange = (bool)p_m_CanApproveChange.GetValue(___m_Checkout);
                            
                            if (canApproveChange && _minimumChangeOnly.Value) break;
                            if (collectedChange >= correctChange) break;
                        } while (true);
                    }

                    m_OnApproveCheckout.Invoke(__instance, null);
                }
                else
                {
                    var m_PosTerminal = (PosTerminal)f_m_PosTerminal.GetValue(___m_Checkout);
                    f_m_Total.SetValue(m_PosTerminal, ___m_Checkout.TotalPrice);
                    m_PosTerminal.Approve();
                }

                return false;
            }
        }
    }
}