﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Framework.DependencyInjection;
using Moq;
using Xunit;

namespace Microsoft.AspNet.DataProtection.XmlEncryption
{
    public class CertificateXmlEncryptorTests
    {
        [Fact]
        public void Encrypt_Decrypt_RoundTrips()
        {
            // Arrange
            var aes = new AesCryptoServiceProvider();
            aes.GenerateKey();

            var serviceCollection = new ServiceCollection();
            var mockInternalEncryptor = new Mock<IInternalCertificateXmlEncryptor>();
            mockInternalEncryptor.Setup(o => o.PerformEncryption(It.IsAny<EncryptedXml>(), It.IsAny<XmlElement>()))
                .Returns<EncryptedXml, XmlElement>((encryptedXml, element) =>
                {
                    encryptedXml.AddKeyNameMapping("theKey", aes); // use symmetric encryption
                    return encryptedXml.Encrypt(element, "theKey");
                });
            serviceCollection.AddInstance<IInternalCertificateXmlEncryptor>(mockInternalEncryptor.Object);

            var mockInternalDecryptor = new Mock<IInternalEncryptedXmlDecryptor>();
            mockInternalDecryptor.Setup(o => o.PerformPreDecryptionSetup(It.IsAny<EncryptedXml>()))
                .Callback<EncryptedXml>(encryptedXml =>
                {
                    encryptedXml.AddKeyNameMapping("theKey", aes); // use symmetric encryption
                });
            serviceCollection.AddInstance<IInternalEncryptedXmlDecryptor>(mockInternalDecryptor.Object);

            var services = serviceCollection.BuildServiceProvider();
            var encryptor = new CertificateXmlEncryptor(services);
            var decryptor = new EncryptedXmlDecryptor(services);

            var originalXml = XElement.Parse(@"<mySecret value='265ee4ea-ade2-43b1-b706-09b259e58b6b' />");

            // Act & assert - run through encryptor and make sure we get back <EncryptedData> element
            var encryptedXmlInfo = encryptor.Encrypt(originalXml);
            Assert.Equal(typeof(EncryptedXmlDecryptor), encryptedXmlInfo.DecryptorType);
            Assert.Equal(XName.Get("EncryptedData", "http://www.w3.org/2001/04/xmlenc#"), encryptedXmlInfo.EncryptedElement.Name);
            Assert.Equal("http://www.w3.org/2001/04/xmlenc#Element", (string)encryptedXmlInfo.EncryptedElement.Attribute("Type"));
            Assert.DoesNotContain("265ee4ea-ade2-43b1-b706-09b259e58b6b", encryptedXmlInfo.EncryptedElement.ToString(), StringComparison.OrdinalIgnoreCase);

            // Act & assert - run through decryptor and make sure we get back the original value
            var roundTrippedElement = decryptor.Decrypt(encryptedXmlInfo.EncryptedElement);
            XmlAssert.Equal(originalXml, roundTrippedElement);
        }
    }
}
