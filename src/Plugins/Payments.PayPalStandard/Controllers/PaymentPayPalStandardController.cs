﻿using Grand.Business.Checkout.Interfaces.Orders;
using Grand.Business.Checkout.Interfaces.Payments;
using Grand.Business.Common.Interfaces.Configuration;
using Grand.Business.Common.Interfaces.Localization;
using Grand.Business.Common.Interfaces.Security;
using Grand.Business.Common.Interfaces.Stores;
using Grand.Web.Common.Controllers;
using Grand.Domain.Orders;
using Grand.Domain.Payments;
using Grand.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Payments.PayPalStandard.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Grand.Business.Common.Interfaces.Logging;
using Grand.Web.Common.Filters;
using Grand.Business.Common.Services.Security;
using Grand.Domain.Common;
using Grand.Domain.Customers;
using Grand.Business.Checkout.Extensions;
using Grand.SharedKernel;
using Grand.Business.Common.Extensions;
using Grand.SharedKernel.Extensions;
using Microsoft.Extensions.Primitives;
using MediatR;
using Grand.Business.Checkout.Queries.Models.Orders;
using Grand.Business.Checkout.Commands.Models.Orders;

namespace Payments.PayPalStandard.Controllers
{

    public class PaymentPayPalStandardController : BasePaymentController
    {
        private readonly IWorkContext _workContext;
        private readonly IStoreService _storeService;
        private readonly ISettingService _settingService;
        private readonly IPaymentService _paymentService;
        private readonly IOrderService _orderService;
        private readonly ITranslationService _translationService;
        private readonly ILogger _logger;
        private readonly IMediator _mediator;
        private readonly IPermissionService _permissionService;
        private readonly IPaymentTransactionService _paymentTransactionService;
        private readonly PaymentSettings _paymentSettings;

        public PaymentPayPalStandardController(IWorkContext workContext,
            IStoreService storeService,
            ISettingService settingService,
            IPaymentService paymentService,
            IOrderService orderService,
            ITranslationService translationService,
            ILogger logger,
            IMediator mediator,
            IPermissionService permissionService,
            IPaymentTransactionService paymentTransactionService,
            PaymentSettings paymentSettings)
        {
            _workContext = workContext;
            _storeService = storeService;
            _settingService = settingService;
            _paymentService = paymentService;
            _orderService = orderService;
            _translationService = translationService;
            _logger = logger;
            _mediator = mediator;
            _permissionService = permissionService;
            _paymentTransactionService = paymentTransactionService;
            _paymentSettings = paymentSettings;
        }

        protected virtual async Task<string> GetActiveStore(IStoreService storeService, IWorkContext workContext)
        {
            var stores = await storeService.GetAllStores();
            if (stores.Count < 2)
                return stores.FirstOrDefault().Id;

            var storeId = workContext.CurrentCustomer.GetUserFieldFromEntity<string>(SystemCustomerFieldNames.AdminAreaStoreScopeConfiguration);
            var store = await storeService.GetStoreById(storeId);

            return store != null ? store.Id : "";
        }

        [AuthorizeAdmin]
        [Area("Admin")]
        public async Task<IActionResult> Configure()
        {
            if (!await _permissionService.Authorize(StandardPermission.ManagePaymentMethods))
                return AccessDeniedView();

            //load settings for a chosen store scope
            var storeScope = await GetActiveStore(_storeService, _workContext);
            var payPalStandardPaymentSettings = _settingService.LoadSetting<PayPalStandardPaymentSettings>(storeScope);

            var model = new ConfigurationModel();
            model.UseSandbox = payPalStandardPaymentSettings.UseSandbox;
            model.BusinessEmail = payPalStandardPaymentSettings.BusinessEmail;
            model.PdtToken = payPalStandardPaymentSettings.PdtToken;
            model.PdtValidateOrderTotal = payPalStandardPaymentSettings.PdtValidateOrderTotal;
            model.AdditionalFee = payPalStandardPaymentSettings.AdditionalFee;
            model.AdditionalFeePercentage = payPalStandardPaymentSettings.AdditionalFeePercentage;
            model.PassProductNamesAndTotals = payPalStandardPaymentSettings.PassProductNamesAndTotals;
            model.DisplayOrder = payPalStandardPaymentSettings.DisplayOrder;

            model.StoreScope = storeScope;
            
            return View("~/Plugins/Payments.PayPalStandard/Views/PaymentPayPalStandard/Configure.cshtml", model);
        }

        [HttpPost]
        [AuthorizeAdmin]
        [Area("Admin")]
        public async Task<IActionResult> Configure(ConfigurationModel model)
        {
            if (!await _permissionService.Authorize(StandardPermission.ManagePaymentMethods))
                return AccessDeniedView();

            if (!ModelState.IsValid)
                return await Configure();

            //load settings for a chosen store scope
            var storeScope = await this.GetActiveStore(_storeService, _workContext);
            var payPalStandardPaymentSettings = _settingService.LoadSetting<PayPalStandardPaymentSettings>(storeScope);

            //save settings
            payPalStandardPaymentSettings.UseSandbox = model.UseSandbox;
            payPalStandardPaymentSettings.BusinessEmail = model.BusinessEmail;
            payPalStandardPaymentSettings.PdtToken = model.PdtToken;
            payPalStandardPaymentSettings.PdtValidateOrderTotal = model.PdtValidateOrderTotal;
            payPalStandardPaymentSettings.AdditionalFee = model.AdditionalFee;
            payPalStandardPaymentSettings.AdditionalFeePercentage = model.AdditionalFeePercentage;
            payPalStandardPaymentSettings.PassProductNamesAndTotals = model.PassProductNamesAndTotals;
            payPalStandardPaymentSettings.DisplayOrder = model.DisplayOrder;

            await _settingService.SaveSetting(payPalStandardPaymentSettings, storeScope);

            //now clear settings cache
            await _settingService.ClearCache();

            Success(_translationService.GetResource("Admin.Plugins.Saved"));

            return await Configure();
        }

        
        private string QueryString(string name)
        {
            if (StringValues.IsNullOrEmpty(HttpContext.Request.Query[name]))
                return default;

            return HttpContext.Request.Query[name].ToString();
        }

        public async Task<IActionResult> PDTHandler(IFormCollection form)
        {
            var tx = QueryString("tx");
            Dictionary<string, string> values;
            string response;

            var processor = _paymentService.LoadPaymentMethodBySystemName("Payments.PayPalStandard") as PayPalStandardPaymentProvider;
            if (processor == null ||
                !processor.IsPaymentMethodActive(_paymentSettings))
                throw new GrandException("PayPal Standard module cannot be loaded");

            if (processor.GetPdtDetails(tx, out values, out response))
            {
                string orderNumber = string.Empty;
                values.TryGetValue("custom", out orderNumber);
                Guid orderNumberGuid = Guid.Empty;
                try
                {
                    orderNumberGuid = new Guid(orderNumber);
                }
                catch { }
                Order order = await _orderService.GetOrderByGuid(orderNumberGuid);
                if (order != null)
                {
                    var paymentTransaction = await _paymentTransactionService.GetByOrdeGuid(orderNumberGuid);

                    double mc_gross = 0;
                    try
                    {
                        mc_gross = double.Parse(values["mc_gross"], new CultureInfo("en-US"));
                    }
                    catch (Exception exc)
                    {
                        _logger.Error("PayPal PDT. Error getting mc_gross", exc);
                    }

                    string payer_status = string.Empty;
                    values.TryGetValue("payer_status", out payer_status);
                    string payment_status = string.Empty;
                    values.TryGetValue("payment_status", out payment_status);
                    string pending_reason = string.Empty;
                    values.TryGetValue("pending_reason", out pending_reason);
                    string mc_currency = string.Empty;
                    values.TryGetValue("mc_currency", out mc_currency);
                    string txn_id = string.Empty;
                    values.TryGetValue("txn_id", out txn_id);
                    string payment_type = string.Empty;
                    values.TryGetValue("payment_type", out payment_type);
                    string payer_id = string.Empty;
                    values.TryGetValue("payer_id", out payer_id);
                    string receiver_id = string.Empty;
                    values.TryGetValue("receiver_id", out receiver_id);
                    string invoice = string.Empty;
                    values.TryGetValue("invoice", out invoice);
                    string payment_fee = string.Empty;
                    values.TryGetValue("payment_fee", out payment_fee);

                    var sb = new StringBuilder();
                    sb.AppendLine("Paypal PDT:");
                    sb.AppendLine("mc_gross: " + mc_gross);
                    sb.AppendLine("Payer status: " + payer_status);
                    sb.AppendLine("Payment status: " + payment_status);
                    sb.AppendLine("Pending reason: " + pending_reason);
                    sb.AppendLine("mc_currency: " + mc_currency);
                    sb.AppendLine("txn_id: " + txn_id);
                    sb.AppendLine("payment_type: " + payment_type);
                    sb.AppendLine("payer_id: " + payer_id);
                    sb.AppendLine("receiver_id: " + receiver_id);
                    sb.AppendLine("invoice: " + invoice);
                    sb.AppendLine("payment_fee: " + payment_fee);

                    var newPaymentStatus = PaypalHelper.GetPaymentStatus(payment_status, pending_reason);
                    sb.AppendLine("New payment status: " + newPaymentStatus);

                    //order note
                    await _orderService.InsertOrderNote(new OrderNote
                    {
                        Note = sb.ToString(),
                        DisplayToCustomer = false,
                        CreatedOnUtc = DateTime.UtcNow,
                        OrderId = order.Id,
                    });

                    //load settings for a chosen store scope
                    var storeScope = await this.GetActiveStore(_storeService, _workContext);
                    var payPalStandardPaymentSettings = _settingService.LoadSetting<PayPalStandardPaymentSettings>(storeScope);

                    //validate order total
                    if (payPalStandardPaymentSettings.PdtValidateOrderTotal && !Math.Round(mc_gross, 2).Equals(Math.Round(order.OrderTotal * order.CurrencyRate, 2)))
                    {
                        string errorStr = string.Format("PayPal PDT. Returned order total {0} doesn't equal order total {1}. Order# {2}.", mc_gross, order.OrderTotal * order.CurrencyRate, order.OrderNumber);
                        _logger.Error(errorStr);

                        //order note
                        await _orderService.InsertOrderNote(new OrderNote
                        {
                            Note = errorStr,
                            OrderId = order.Id,
                            DisplayToCustomer = false,
                            CreatedOnUtc = DateTime.UtcNow
                        });

                        return RedirectToAction("Index", "Home", new { area = "" });
                    }

                    //mark order as paid
                    if (newPaymentStatus == PaymentStatus.Paid)
                    {
                        if (await _mediator.Send(new CanMarkPaymentTransactionAsPaidQuery() { PaymentTransaction = paymentTransaction }))
                        {
                            paymentTransaction.AuthorizationTransactionId = txn_id;
                            await _paymentTransactionService.UpdatePaymentTransaction(paymentTransaction);
                            await _mediator.Send(new MarkAsPaidCommand() { PaymentTransaction = paymentTransaction });
                        }
                    }
                }

                return RedirectToRoute("CheckoutCompleted", new { orderId = order.Id });
            }
            else
            {
                string orderNumber = string.Empty;
                values.TryGetValue("custom", out orderNumber);
                Guid orderNumberGuid = Guid.Empty;
                try
                {
                    orderNumberGuid = new Guid(orderNumber);
                }
                catch { }
                Order order = await _orderService.GetOrderByGuid(orderNumberGuid);
                if (order != null)
                {
                    //order note
                    await _orderService.InsertOrderNote(new OrderNote
                    {
                        Note = "PayPal PDT failed. " + response,
                        DisplayToCustomer = false,
                        CreatedOnUtc = DateTime.UtcNow,
                        OrderId = order.Id,
                    });
                }
                return RedirectToAction("Index", "Home", new { area = "" });
            }
        }

        public async Task<IActionResult> IPNHandler()
        {
            string strRequest = string.Empty;
            using (var stream = new StreamReader(Request.Body))
            {
                strRequest = await stream.ReadToEndAsync();
            }
            Dictionary<string, string> values;
            var processor = _paymentService.LoadPaymentMethodBySystemName("Payments.PayPalStandard") as PayPalStandardPaymentProvider;
            if (processor == null ||
                !processor.IsPaymentMethodActive(_paymentSettings))
                throw new GrandException("PayPal Standard module cannot be loaded");

            if (processor.VerifyIpn(strRequest, out values))
            {
                #region values
                double mc_gross = 0;
                try
                {
                    mc_gross = double.Parse(values["mc_gross"], new CultureInfo("en-US"));
                }
                catch { }

                string payer_status = string.Empty;
                values.TryGetValue("payer_status", out payer_status);
                string payment_status = string.Empty;
                values.TryGetValue("payment_status", out payment_status);
                string pending_reason = string.Empty;
                values.TryGetValue("pending_reason", out pending_reason);
                string mc_currency = string.Empty;
                values.TryGetValue("mc_currency", out mc_currency);
                string txn_id = string.Empty;
                values.TryGetValue("txn_id", out txn_id);
                string txn_type = string.Empty;
                values.TryGetValue("txn_type", out txn_type);
                string rp_invoice_id = string.Empty;
                values.TryGetValue("rp_invoice_id", out rp_invoice_id);
                string payment_type = string.Empty;
                values.TryGetValue("payment_type", out payment_type);
                string payer_id = string.Empty;
                values.TryGetValue("payer_id", out payer_id);
                string receiver_id = string.Empty;
                values.TryGetValue("receiver_id", out receiver_id);
                string invoice = string.Empty;
                values.TryGetValue("invoice", out invoice);
                string payment_fee = string.Empty;
                values.TryGetValue("payment_fee", out payment_fee);

                #endregion

                var sb = new StringBuilder();
                sb.AppendLine("Paypal IPN:");
                foreach (KeyValuePair<string, string> kvp in values)
                {
                    sb.AppendLine(kvp.Key + ": " + kvp.Value);
                }

                var newPaymentStatus = PaypalHelper.GetPaymentStatus(payment_status, pending_reason);
                sb.AppendLine("New payment status: " + newPaymentStatus);

                switch (txn_type)
                {
                    case "recurring_payment_profile_created":
                        //do nothing here
                        break;
                    case "recurring_payment":
                        //do nothing here
                        break;
                    default:
                        #region Standard payment
                        {
                            string orderNumber = string.Empty;
                            values.TryGetValue("custom", out orderNumber);
                            Guid orderNumberGuid = Guid.Empty;
                            try
                            {
                                orderNumberGuid = new Guid(orderNumber);
                            }
                            catch
                            {
                            }

                            var order = await _orderService.GetOrderByGuid(orderNumberGuid);
                            if (order != null)
                            {
                                //order note
                                await _orderService.InsertOrderNote(new OrderNote
                                {
                                    Note = sb.ToString(),
                                    DisplayToCustomer = false,
                                    CreatedOnUtc = DateTime.UtcNow,
                                    OrderId = order.Id,
                                });
                                var paymentTransaction = await _paymentTransactionService.GetByOrdeGuid(order.OrderGuid);

                                switch (newPaymentStatus)
                                {
                                    case PaymentStatus.Pending:
                                        {
                                        }
                                        break;
                                    case PaymentStatus.Authorized:
                                        {
                                            //validate order total
                                            if (Math.Round(mc_gross, 2).Equals(Math.Round(order.OrderTotal, 2)))
                                            {
                                                //valid
                                                if (await _mediator.Send(new CanMarkPaymentTransactionAsAuthorizedQuery() { PaymentTransaction = paymentTransaction }))
                                                {
                                                    await _mediator.Send(new MarkAsAuthorizedCommand() { PaymentTransaction = paymentTransaction });
                                                }
                                            }
                                            else
                                            {
                                                //not valid
                                                string errorStr = string.Format("PayPal IPN. Returned order total {0} doesn't equal order total {1}. Order# {2}.", mc_gross, order.OrderTotal * order.CurrencyRate, order.Id);
                                                //log
                                                _logger.Error(errorStr);
                                                //order note
                                                await _orderService.InsertOrderNote(new OrderNote
                                                {
                                                    Note = errorStr,
                                                    DisplayToCustomer = false,
                                                    CreatedOnUtc = DateTime.UtcNow,
                                                    OrderId = order.Id,
                                                });
                                            }
                                        }
                                        break;
                                    case PaymentStatus.Paid:
                                        {
                                            //validate order total
                                            if (Math.Round(mc_gross, 2).Equals(Math.Round(order.OrderTotal, 2)))
                                            {
                                                //valid
                                                if (await _mediator.Send(new CanMarkPaymentTransactionAsPaidQuery() { PaymentTransaction = paymentTransaction }))
                                                {
                                                    paymentTransaction.AuthorizationTransactionId = txn_id;
                                                    await _paymentTransactionService.UpdatePaymentTransaction(paymentTransaction);

                                                    await _mediator.Send(new MarkAsPaidCommand() { PaymentTransaction = paymentTransaction });
                                                }
                                            }
                                            else
                                            {
                                                //not valid
                                                string errorStr = string.Format("PayPal IPN. Returned order total {0} doesn't equal order total {1}. Order# {2}.", mc_gross, order.OrderTotal * order.CurrencyRate, order.Id);
                                                //log
                                                _logger.Error(errorStr);
                                                //order note
                                                await _orderService.InsertOrderNote(new OrderNote
                                                {
                                                    Note = errorStr,
                                                    DisplayToCustomer = false,
                                                    CreatedOnUtc = DateTime.UtcNow,
                                                    OrderId = order.Id,
                                                });
                                            }
                                        }
                                        break;
                                    case PaymentStatus.Refunded:
                                        {
                                            var totalToRefund = Math.Abs(mc_gross);
                                            if (totalToRefund > 0 && Math.Round(totalToRefund, 2).Equals(Math.Round(order.OrderTotal, 2)))
                                            {
                                                //refund
                                                if (await _mediator.Send(new CanRefundOfflineQuery() { PaymentTransaction = paymentTransaction }))
                                                {
                                                    await _mediator.Send(new RefundOfflineCommand() { PaymentTransaction = paymentTransaction });
                                                }
                                            }
                                            else
                                            {
                                                //partial refund
                                                if (await _mediator.Send(new CanPartiallyRefundOfflineQuery() { PaymentTransaction = paymentTransaction, AmountToRefund = totalToRefund }))
                                                {
                                                    await _mediator.Send(new PartiallyRefundOfflineCommand() { PaymentTransaction = paymentTransaction, AmountToRefund = totalToRefund });
                                                }
                                            }
                                        }
                                        break;
                                    case PaymentStatus.Voided:
                                        {
                                            if (await _mediator.Send(new CanVoidOfflineQuery() { PaymentTransaction = paymentTransaction }))
                                            {
                                                await _mediator.Send(new VoidOfflineCommand() { PaymentTransaction = paymentTransaction });
                                            }
                                        }
                                        break;
                                    default:
                                        break;
                                }
                            }
                            else
                            {
                                _logger.Error("PayPal IPN. Order is not found", new GrandException(sb.ToString()));
                            }
                        }
                        #endregion
                        break;
                }
            }
            else
            {
                _logger.Error("PayPal IPN failed.", new GrandException(strRequest));
            }

            //nothing should be rendered to visitor
            return Content("");
        }

        public async Task<IActionResult> CancelOrder(IFormCollection form)
        {
            var order = (await _orderService.SearchOrders(storeId: _workContext.CurrentStore.Id,
                customerId: _workContext.CurrentCustomer.Id, pageSize: 1)).FirstOrDefault();
            if (order != null)
                return RedirectToRoute("OrderDetails", new { orderId = order.Id });

            return RedirectToRoute("HomePage");
        }
    }
}