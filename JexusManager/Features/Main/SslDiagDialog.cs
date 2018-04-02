﻿// Copyright (c) Lex Li. All rights reserved.
// 
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using Microsoft.Web.Administration;
using Microsoft.Web.Management.Client.Win32;
using System;
using System.Linq;
using System.Security.Cryptography;

namespace JexusManager.Features.Main
{
    using System.Drawing;
    using System.IO;
    using System.Security.Cryptography.X509Certificates;
    using Microsoft.Win32;

    using Org.BouncyCastle.Utilities.Encoders;

    using System.Reactive.Disposables;
    using System.Reactive.Linq;
    using System.Collections.Generic;

    public partial class SslDiagDialog : DialogForm
    {
        public SslDiagDialog(IServiceProvider provider, ServerManager server)
            : base(provider)
        {
            InitializeComponent();

            // from https://www.ibm.com/support/knowledgecenter/SSLTBW_2.1.0/com.ibm.zos.v2r1.ikya100/sigalg.htm
            var wellKnownSignatureAlgorithms = new Dictionary<string, bool>
            {
                { "1.2.840.113549.1.1.5", false }, // sha1RSA, not secure
                { "1.2.840.113549.1.1.14", true }, // sha224RSA, secure
                { "1.2.840.113549.1.1.11", true }, // sha256RSA, secure
                { "1.2.840.113549.1.1.12", true }, // sha384RSA, secure
                { "1.2.840.113549.1.1.13", true }, // sha512RSA, secure
                { "1.2.840.10040.4.3", false }, // sha1DSA, not secure
                { "1.2.840.10045.4.1", false }, // sha1ECDSA, not secure
                { "1.2.840.10045.4.3.1", true }, // sha224ECDSA, secure
                { "1.2.840.10045.4.3.2", true }, // sha256ECDSA, secure
                { "1.2.840.10045.4.3.3", true }, // sha384ECDSA, secure
                { "1.2.840.10045.4.3.4", true }, // sha512ECDSA, secure
            };

            var container = new CompositeDisposable();
            FormClosed += (sender, args) => container.Dispose();

            container.Add(
                Observable.FromEventPattern<EventArgs>(btnGenerate, "Click")
                .ObserveOn(System.Threading.SynchronizationContext.Current)
                .Subscribe(evt =>
                {
                    txtResult.Clear();
                    try
                    {
                        Debug($"System Time: {DateTime.Now}");
                        Debug($"Processor Architecture: {Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE")}");
                        Debug($"OS: {Environment.OSVersion}");
                        Debug($"{server.Type}");
                        Debug(string.Empty);
                        Debug($"SERVER SSL PROTOCOLS{Environment.NewLine}");
                        bool ssl10Enabled = GetProtocol("PCT 1.0");
                        Debug($"PCT 1.0: {ssl10Enabled}");
                        if (ssl10Enabled)
                        {
                            Warn("PCT 1.0 is not secure. Please disable it.");
                        }

                        bool ssl20Enabled = GetProtocol("SSL 2.0");
                        Debug($"SSL 2.0: {ssl20Enabled}");
                        if (ssl20Enabled)
                        {
                            Warn("SSL 2.0 is not secure. Please disable it.");
                        }

                        bool ssl30Enabled = GetProtocol("SSL 3.0");
                        Debug($"SSL 3.0: {ssl30Enabled}");
                        if (ssl30Enabled)
                        {
                            Warn("SSL 3.0 is not secure. Please disable it.");
                        }

                        Debug($"TLS 1.0: {GetProtocol("TLS 1.0")}");
                        Debug($"TLS 1.1: {GetProtocol("TLS 1.1")}");
                        Debug($"TLS 1.2: {GetProtocol("TLS 1.2")}");
                        Debug($"SChannel EventLogging: {GetEventLogging()} (hex)");
                        Warn($"To tune TLS related settings, try out IIS Crypto from https://www.nartac.com/Products/IISCrypto/.");
                        Debug("-----");

                        foreach (Site site in server.Sites)
                        {
                            Debug($"[W3SVC/{site.Id}]");
                            Debug($"ServerComment  : {site.Name}");
                            Debug($"ServerAutoStart: {site.ServerAutoStart}");
                            Debug($"ServerState    : {site.State}");
                            Debug(string.Empty);
                            foreach (Binding binding in site.Bindings)
                            {
                                Info($"BINDING: {binding.Protocol} {binding}");
                                if (binding.Protocol == "https")
                                {
                                    if (binding.CertificateHash == null)
                                    {
                                        // SNI mapping missing.
                                        Debug($"SSL Flags: {binding.SslFlags}");
                                        if (binding.SslFlags == SslFlags.Sni)
                                        {
                                            Error(
                                                $"Cannot find {binding.Host}:{binding.EndPoint.Port} combination for this SNI binding.");
                                        }
                                        else
                                        {
                                            var querySslCertificateInfo = Microsoft.Web.Administration.NativeMethods.QuerySslCertificateInfo(
                                                binding.EndPoint);
                                            Error(
                                                querySslCertificateInfo == null
                                                    ? $"Cannot find {binding.EndPoint} combination for this IP based binding."
                                                    : $"Cannot find certificate with thumpprint {querySslCertificateInfo.Hash} in store {querySslCertificateInfo.StoreName}.");
                                        }

                                        Debug(string.Empty);
                                        continue;
                                    }
                                    
                                    var hashString = Hex.ToHexString(binding.CertificateHash);
                                    Debug($"SSLCertHash: {hashString}");
                                    if (site.Server.SupportsSni)
                                    {
                                        Debug($"SSL Flags: {binding.SslFlags}");
                                    }

                                    Debug("Testing EndPoint: 127.0.0.1");

                                    var personal = new X509Store(binding.CertificateStoreName, StoreLocation.LocalMachine);
                                    try
                                    {
                                        personal.Open(OpenFlags.MaxAllowed);
                                        var selectedItem = personal.Certificates.Find(X509FindType.FindByThumbprint, hashString, false);
                                        if (selectedItem.Count == 0)
                                        {
                                            Error($"Cannot find certificate with thumbprint {hashString} in store {binding.CertificateStoreName}.");
                                        }
                                        else
                                        {
                                            var cert = selectedItem[0];
                                            Debug($"#CertName: {cert.FriendlyName}");
                                            Debug($"#Version: {cert.Version}");
                                            if (cert.HasPrivateKey)
                                            {
                                                if (PublicNativeMethods.IsProcessElevated)
                                                {
                                                    var newHandle = IntPtr.Zero;
                                                    int newCount = 0;
                                                    var shouldRelease = false;
                                                    if (NativeMethods.CryptAcquireCertificatePrivateKey(
                                                        cert.Handle, 0, IntPtr.Zero, ref newHandle, ref newCount,
                                                        ref shouldRelease))
                                                    {
                                                        Debug(
                                                            "#You have a private key that corresponds to this certificate.");
                                                    }
                                                    else
                                                    {
                                                        Error("#You have a private key that corresponds to this certificate but CryptAcquireCertificatePrivateKey failed.");
                                                        RollbarDotNet.Rollbar.Report(
                                                            "CryptAcquireCertificatePrivateKey failed");
                                                    }

                                                    if (shouldRelease)
                                                    {
                                                        NativeMethods.CloseHandle(newHandle);
                                                    }
                                                }
                                                else
                                                {
                                                    Warn("It seems that you have a private key that corresponds to this certificate. Please run Jexus Manager as administrator and SSL Diag can report in more details.");
                                                }
                                            }
                                            else
                                            {
                                                Error(
                                                    "#You don't have a private key that corresponds to this certificate.");
                                            }

                                            var key = cert.PublicKey.Key;
                                            var signatureAlgorithm = cert.SignatureAlgorithm;
                                            Debug($"#Signature Algorithm: {signatureAlgorithm.FriendlyName}");
                                            if (wellKnownSignatureAlgorithms.ContainsKey(signatureAlgorithm.Value))
                                            {
                                                if (!wellKnownSignatureAlgorithms[signatureAlgorithm.Value])
                                                {
                                                    Warn("Modern web browsers require signature algorithm to be secure. This signature algorithm is not secure, and might trigger warnings and/or errors.");
                                                }
                                            }
                                            else
                                            {
                                                Warn("This certificate uses a not-well-known signature algorithm, which might not be supported by all web browsers and servers.");
                                            }

                                            Debug($"#Key Exchange Algorithm: {key.KeyExchangeAlgorithm} Key Size: {key.KeySize}");
                                            Debug($"#Subject: {cert.Subject}");
                                            Debug($"#Issuer: {cert.Issuer}");
                                            Debug($"#Validity: From {cert.NotBefore:G} To {cert.NotAfter:G}");
                                            var now = DateTime.UtcNow;
                                            if (now < cert.NotBefore)
                                            {
                                                Warn("This certificate is not yet valid.");
                                            }

                                            if (cert.NotAfter < now)
                                            {
                                                Error("This certificate is already expired.");
                                            }

                                            Debug($"#Serial Number: {cert.SerialNumber}");
                                            Debug($"DS Mapper Usage: {(binding.UseDsMapper ? "Enabled" : "Disabled")}");
                                            Debug($"Archived: {cert.Archived}");

                                            var hasSAN = false;
                                            foreach (var extension in cert.Extensions)
                                            {
                                                if (extension.Oid.Value == "2.5.29.15")
                                                {
                                                    Debug($"#Key Usage: {((X509KeyUsageExtension)extension).KeyUsages}");
                                                    continue;
                                                }

                                                if (extension.Oid.Value == "2.5.29.37")
                                                {
                                                    var usages = ((X509EnhancedKeyUsageExtension)extension).EnhancedKeyUsages;
                                                    var enhancedKeyUsage = usages.Cast<Oid>().Select(usage => $"{usage.FriendlyName} ({usage.Value})")
                                                        .Combine(",");

                                                    Debug($"#Enhanced Key Usage: {enhancedKeyUsage}");
                                                    continue;
                                                }

                                                if (extension.Oid.Value == "2.5.29.17")
                                                {
                                                    var data = extension.RawData;
                                                    AsnEncodedData asndata = new AsnEncodedData(extension.Oid, extension.RawData);
                                                    var name = asndata.Format(true).TrimEnd();
                                                    Debug($"#Subject Alternative Name: {name}");
                                                    hasSAN = true;
                                                    continue;
                                                }

                                                if (extension.Oid.FriendlyName == "Basic Constraints")
                                                {
                                                    var ext = (X509BasicConstraintsExtension)extension;
                                                    Debug(
                                                        $"#Basic Constraints: Subject Type={(ext.CertificateAuthority ? "CA" : "End Entity")}, Path Length Constraint={(ext.HasPathLengthConstraint ? ext.PathLengthConstraint.ToString() : "None")}");
                                                }
                                            }

                                            if (!hasSAN)
                                            {
                                                Warn("Modern web browsers require Subject Alternative Name extension to present. This certificate does not have SAN extension, so might trigger warnings and/or errors.");
                                            }

                                            X509Chain chain = X509Chain.Create();
                                            chain.ChainPolicy = new X509ChainPolicy
                                            {
                                                RevocationMode = X509RevocationMode.Online
                                            };
                                            bool valid = chain.Build(cert);
                                            if (valid)
                                            {
                                                Debug("Certificate verified.");
                                            }
                                            else
                                            {
                                                Error("Certificate validation failed.");
                                            }

                                            foreach (var item in chain.ChainStatus)
                                            {
                                                Warn(item.StatusInformation);
                                            }
                                        }

                                        personal.Close();
                                    }
                                    catch (CryptographicException ex)
                                    {
                                        if (ex.HResult != Microsoft.Web.Administration.NativeMethods.NonExistingStore)
                                        {
                                            throw;
                                        }

                                        Error($"Invalid certificate store {binding.CertificateStoreName}.");
                                    }
                                }

                                Debug(string.Empty);
                            }
                        }
                    }
                    catch (CryptographicException ex)
                    {
                        Debug(ex.ToString());
                        RollbarDotNet.Rollbar.Report(ex, custom: new Dictionary<string, object> { { "hResult", ex.HResult } });
                    }
                    catch (Exception ex)
                    {
                        Debug(ex.ToString());
                        RollbarDotNet.Rollbar.Report(ex);
                    }
                }));

           container.Add(
                Observable.FromEventPattern<EventArgs>(btnSave, "Click")
                .ObserveOn(System.Threading.SynchronizationContext.Current)
                .Subscribe(evt =>
                {
                    var fileName = DialogHelper.ShowSaveFileDialog(null, "Text Files|*.txt|All Files|*.*");
                    if (string.IsNullOrEmpty(fileName))
                    {
                        return;
                    }
                    
                    File.WriteAllText(fileName, txtResult.Text);
                }));

           container.Add(
                Observable.FromEventPattern<EventArgs>(btnVerify, "Click")
                .ObserveOn(System.Threading.SynchronizationContext.Current)
                .Subscribe(evt =>
                {
                    txtResult.Clear();
                }));
        }

        private void Debug(string text)
        {
            txtResult.AppendText(text);
            txtResult.AppendText(Environment.NewLine);
        }

        private void Error(string text)
        {
            var defaultColor = txtResult.SelectionColor;
            txtResult.SelectionColor = Color.Red;
            txtResult.AppendText(text);
            txtResult.SelectionColor = defaultColor;
            txtResult.AppendText(Environment.NewLine);
        }

        private void Warn(string text)
        {
            var defaultColor = txtResult.SelectionColor;
            txtResult.SelectionColor = Color.Green;
            txtResult.AppendText(text);
            txtResult.SelectionColor = defaultColor;
            txtResult.AppendText(Environment.NewLine);
        }

        private void Info(string text)
        {
            var defaultColor = txtResult.SelectionColor;
            txtResult.SelectionColor = Color.Blue;
            txtResult.AppendText(text);
            txtResult.SelectionColor = defaultColor;
            txtResult.AppendText(Environment.NewLine);
        }

        private int GetEventLogging()
        {
            if (Helper.IsRunningOnMono())
            {
                return -1;
            }

            // https://support.microsoft.com/en-us/kb/260729
            var key = Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Control\SecurityProviders\SCHANNEL");
            if (key == null)
            {
                return 1;
            }

            var value = (int)key.GetValue("EventLogging", 1);
            return value;
        }

        private static bool GetProtocol(string protocol)
        {
            if (Helper.IsRunningOnMono())
            {
                return false;
            }

            // https://support.microsoft.com/en-us/kb/187498
            var key =
                Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Protocols\{protocol}\Server");
            if (key == null)
            {
                return true;
            }

            var value = (int)key.GetValue("Enabled", 1);
            var enabled = value == 1;
            return enabled;
        }

        private void BtnHelpClick(object sender, EventArgs e)
        {
            DialogHelper.ProcessStart("http://www.jexusmanager.com/en/latest/tutorials/ssl-diagnostics.html");
        }

        private void SslDiagDialogHelpButtonClicked(object sender, System.ComponentModel.CancelEventArgs e)
        {
            BtnHelpClick(null, EventArgs.Empty);
        }
    }
}
