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
        // private static ConfigEntry<bool> _zeroClick;

        private void Awake()
        {
            _fastCheckout = Config.Bind("Logic", "Fast Checkout", false,
                "By enabling this option, all your checkouts will be instant. With a single click, you'll scan all items, receive payment, and finalize the transaction");
            _fastCheckoutKey = Config.Bind("Keys", "Fast Checkout Key", KeyCode.LeftShift,
                "With this key, you'll only perform a fast checkout while it's pressed. To use it, hold down this key while clicking to scan an item, and the entire process of scanning the remaining items, receiving payment, and clearing the customer will occur. Note that if the Fast Checkout option is activated, pressing this key will have no effect");
            // _zeroClick = Config.Bind("Logic", "Zero Click", false, "Do fast checkouts without clicks or pressing any key");
            _minimumChangeOnly = Config.Bind("Logic", "Minimum Change Only", false,
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

        private const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance |
                                           BindingFlags.DeclaredOnly;

        [HarmonyPatch(typeof(CheckoutInteraction), "InteractWithProduct")]
        public static class CheckoutInteraction_InteractWithProduct
        {
            private static readonly FieldInfo f_m_Customers = typeof(Checkout).GetField("m_Customers", flags);

            private static readonly FieldInfo f_m_PaymentMoney =
                typeof(Customer).GetField("m_PaymentMoney", flags);

            private static readonly FieldInfo f_m_PaymentViaCreditCard =
                typeof(Customer).GetField("m_PaymentViaCreditCard", flags);

            private static readonly FieldInfo f_m_CollectedChange =
                typeof(Checkout).GetField("m_CollectedChange", flags);

            private static readonly FieldInfo f_m_CorrectChange =
                typeof(Checkout).GetField("m_CorrectChange", flags);

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

                var viaCreditCard = (bool)f_m_PaymentViaCreditCard.GetValue(currentCustomer);

                if (viaCreditCard)
                {
                    ___m_Checkout.TookCustomersCard();
                }
                else
                {
                    var paymentMoneyGameObject = (GameObject)f_m_PaymentMoney.GetValue(currentCustomer);
                    var currentMoney = paymentMoneyGameObject.GetComponent<MoneyPack>();
                    ___m_Checkout.TookCustomersCash(currentMoney.Value);
                }

                if (!viaCreditCard)
                {
                    var correctChange = (float)f_m_CorrectChange.GetValue(___m_Checkout);
                    var minimumChange = (float)Math.Round(Math.Max(
                        correctChange * 0.5,
                        correctChange - ___m_Checkout.TotalPrice * 0.5
                    ), 2);

                    f_m_CollectedChange.SetValue(___m_Checkout,
                        _minimumChangeOnly.Value ? minimumChange : correctChange);
                    if (_minimumChangeOnly.Value && !(bool)p_m_CanApproveChange.GetValue(___m_Checkout))
                    {
                        f_m_CollectedChange.SetValue(___m_Checkout, minimumChange + 0.01f);
                    }
                    m_OnApproveCheckout.Invoke(CheckoutInteraction.Instance, null);
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

        // [HarmonyPatch(typeof(Checkout), "StartCheckout")]
        // public static class Checkout_StartCheckout
        // {
        //     private static readonly MethodInfo m_OnUse = typeof(CheckoutInteraction).GetMethod("OnUse", flags);
        //     
        //     public static void Postfix(Checkout __instance)
        //     {
        //         if (!__instance.HasCashier && __instance.CurrentState is Checkout.State.SCANNING or Checkout.State.IDLE)
        //         {
        //             _zeroClick.ConfigFile.Reload();
        //             if (_zeroClick.Value)
        //             {
        //                 m_OnUse.Invoke(CheckoutInteraction.Instance, new object[] { true });
        //             }
        //         }
        //     }
        // }
    }
}