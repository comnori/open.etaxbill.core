﻿/*
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
GNU General Public License for more details.
You should have received a copy of the GNU General Public License
along with this program.If not, see<http://www.gnu.org/licenses/>.
*/

using System;
using System.Data;
using System.Threading;
using NpgsqlTypes;
using OdinSdk.BaseLib.Configuration;
using OpenTax.Engine.Library.Data.POSTGRESQL;
using OpenTax.Engine.Library;

namespace OpenTax.Engine.Mailer
{
    public class Worker : IDisposable
    {
        //-------------------------------------------------------------------------------------------------------------------------
        // 
        //-------------------------------------------------------------------------------------------------------------------------
        private OpenTax.Channel.Interface.IMailer m_imailer = null;
        private OpenTax.Channel.Interface.IMailer IMailer
        {
            get
            {
                if (m_imailer == null)
                    m_imailer = new OpenTax.Channel.Interface.IMailer();

                return m_imailer;
            }
        }

        private OpenTax.Engine.Mailer.Engine m_emailer = null;
        private OpenTax.Engine.Mailer.Engine EMailer
        {
            get
            {
                if (m_emailer == null)
                    m_emailer = new OpenTax.Engine.Mailer.Engine();

                return m_emailer;
            }
        }

        private OpenTax.Engine.Library.UAppHelper m_appHelper = null;
        public OpenTax.Engine.Library.UAppHelper UAppHelper
        {
            get
            {
                if (m_appHelper == null)
                    m_appHelper = new OpenTax.Engine.Library.UAppHelper(IMailer.Manager);

                return m_appHelper;
            }
        }

        private OpenTax.Engine.Library.Data.POSTGRESQL.PgDataHelper m_dataHelper = null;
        private OpenTax.Engine.Library.Data.POSTGRESQL.PgDataHelper LSQLHelper
        {
            get
            {
                if (m_dataHelper == null)
                    m_dataHelper = new OpenTax.Engine.Library.Data.POSTGRESQL.PgDataHelper();
                return m_dataHelper;
            }
        }

        //-------------------------------------------------------------------------------------------------------------------------
        // 
        //-------------------------------------------------------------------------------------------------------------------------
        private System.Threading.Timer SendingTimer;

        private void SenderWorking()
        {
            // clear 'X' flag
            {
                int _norec = EMailer.ClearXFlag();
                ELogger.SNG.WriteLog(String.Format("While starting, updated to 'F' {0} record(s) that isMailSending was 'X'.", _norec));
            }

            // Do not use using statement
            AutoResetEvent _autoEvent = new AutoResetEvent(false);
            {
                SendingTimer = new Timer(SenderWakeup, _autoEvent, TimeSpan.FromSeconds(1).Milliseconds, Timeout.Infinite);

                int _iteration = 0;

                ELogger.SNG.WriteLog
                    (
                        String.Format
                        (
                            "productid->{0}, liveServer->{1}, mailerDueTime->{2}, debugMode->{3}, mailSniffing->{4}, soapFiltering->{5}, eMailFolder->{6}",
                            UAppHelper.QMaster.ProductId, UAppHelper.LiveServer, UAppHelper.MailerDueTime, CfgHelper.SNG.DebugMode,
                            UAppHelper.MailSniffing, UAppHelper.SoapFiltering, UAppHelper.eMailFolder
                        )
                    );

                while (_autoEvent.WaitOne() == true && ShouldStop == false)
                    IMailer.WriteDebug(String.Format("waiting: {0}...", ++_iteration));

                SendingTimer.Dispose();
            }
        }

        private void SenderWakeup(object stateInfo)
        {
            var _autoEvent = (AutoResetEvent)stateInfo;

            try
            {
                IMailer.WriteDebug("wakeup...");

                DateTime _fromDay = UTextHelper.SNG.GetFirstDayOfLastMonth();
                DateTime _tillDay = UTextHelper.SNG.GetLastDayOfThisMonth();

                UTextHelper.SNG.GetSendingRange(ref _fromDay, ref _tillDay);

                // check table for auto-mailing
                var _sqlstr
                    = "SELECT b.invoicerId, COUNT(b.invoicerId) as norec, @fromDay as fromDay, @tillDay as tillDay "
                    + "  FROM TB_eTAX_ISSUING a INNER JOIN TB_eTAX_INVOICE b "
                    + "    ON a.issueId=b.issueId "
                    + " WHERE (a.isInvoiceeMail != @isInvoiceeMail OR a.isProviderMail != @isProviderMail) "
                    + "   AND b.issueDate>=@fromDay AND b.issueDate<=@tillDay "
                    + " GROUP BY b.invoicerId";

                var _dbps = new PgDatParameters();
                {
                    _dbps.Add("@isInvoiceeMail", NpgsqlDbType.Varchar, "T");
                    _dbps.Add("@isProviderMail", NpgsqlDbType.Varchar, "T");
                    _dbps.Add("@fromDay", NpgsqlDbType.TimestampTz, _fromDay);
                    _dbps.Add("@tillDay", NpgsqlDbType.TimestampTz, _tillDay);
                }

                var _ds = LSQLHelper.SelectDataSet(UAppHelper.ConnectionString, _sqlstr, _dbps);
                if (LSQLHelper.IsNullOrEmpty(_ds) == false)
                {
                    var _rows = _ds.Tables[0].Rows;
                    ELogger.SNG.WriteLog(String.Format("selected invoicer(s): {0} ", _rows.Count));

                    var _doneEvents = new ThreadPoolWait[_rows.Count];
                    for (int i = 0; i < _rows.Count; i++)
                    {
                        _doneEvents[i] = new ThreadPoolWait();
                        _doneEvents[i].QueueUserWorkItem(MailerCallback, _rows[i]);

                        if (Environment.UserInteractive == true)
                            _doneEvents[i].WaitOne();
                    }

                    ThreadPoolWait.WaitForAll(_doneEvents);
                }
            }
            catch (MailerException ex)
            {
                ELogger.SNG.WriteLog(ex);
            }
            catch (Exception ex)
            {
                ELogger.SNG.WriteLog(ex);
            }
            finally
            {
                IMailer.WriteDebug("sleep...");

                SendingTimer.Change(UAppHelper.MailerDueTime, Timeout.Infinite);
                _autoEvent.Set();
            }
        }

        private void MailerCallback(Object p_invoicer)
        {
            var _sendingDay = DateTime.Now;

            try
            {
                DataRow _invoicerRow = (DataRow)p_invoicer;

                string _invoicerId = Convert.ToString(_invoicerRow["invoicerId"]);
                int _noInvoicee = Convert.ToInt32(_invoicerRow["norec"]);

                DateTime _fromDay = Convert.ToDateTime(_invoicerRow["fromDay"]);
                DateTime _tillDay = Convert.ToDateTime(_invoicerRow["tillDay"]);

                DataSet _customer_set = EMailer.GetCustomerSet(_invoicerId);
                DataRow _customerRow = _customer_set.Tables[0].Rows[0];

                decimal _fromSendingDay = Convert.ToDecimal(_customerRow["sendFromDay"]);
                decimal _tillSendingDay = Convert.ToDecimal(_customerRow["sendTillDay"]);

                string _sendingType = Convert.ToString(_customerRow["sendingType"]);
                if (_sendingType == "01" || _sendingType == "03")
                {
                    if (_sendingType == "01")
                    {
                        decimal _today = _sendingDay.Day;
                        if (_today < _fromSendingDay || _today > _tillSendingDay)
                            throw new MailerException(
                                    String.Format(
                                        "out of range send-period: '{0}', fromDay->{1}, tillDay->{2}, toDay->{3}",
                                        _invoicerId, _fromSendingDay, _tillSendingDay, _today
                                    )
                                );
                    }

                    EMailer.DoMailSend(_invoicerId, _noInvoicee, _fromDay, _tillDay);
                }
                else
                {
                    IMailer.WriteDebug(String.Format("invoicer '{0}' is skipped {1} record(s) because send-type is {2}.", _invoicerId, _noInvoicee, _sendingType));
                }
            }
            catch (MailerException ex)
            {
                ELogger.SNG.WriteLog(ex);
            }
            catch (Exception ex)
            {
                ELogger.SNG.WriteLog(ex);
            }
        }

        //-------------------------------------------------------------------------------------------------------------------------
        // 
        //-------------------------------------------------------------------------------------------------------------------------
        private System.Threading.Thread SendingThread;
        private volatile bool ShouldStop;

        /// <summary>
        /// 
        /// </summary>
        public void Start()
        {
            ShouldStop = false;

            SendingThread = new Thread(SenderWorking);
            SendingThread.Start();

            while (SendingThread.IsAlive == false)
                Thread.Sleep(100);

            Thread.Sleep(1);
        }

        /// <summary>
        /// 
        /// </summary>
        public void Stop()
        {
            ShouldStop = true;

            if (SendingThread != null)
            {
                SendingThread.Abort();
                Thread.Sleep(1000);
            }
        }

        //-------------------------------------------------------------------------------------------------------------------------
        // 
        //-------------------------------------------------------------------------------------------------------------------------

        /// <summary>
        /// 
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="disposing"></param>
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (m_imailer != null)
                {
                    m_imailer.Dispose();
                    m_imailer = null;
                }
                if (SendingTimer != null)
                {
                    SendingTimer.Dispose();
                    SendingTimer = null;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        ~Worker()
        {
            Dispose(false);
        }

        //-------------------------------------------------------------------------------------------------------------------------
        // 
        //-------------------------------------------------------------------------------------------------------------------------
    }
}