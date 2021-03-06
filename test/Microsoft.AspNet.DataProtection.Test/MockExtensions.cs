﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Xml.Linq;
using Microsoft.AspNet.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNet.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.AspNet.DataProtection.XmlEncryption;
using Moq;

namespace Microsoft.AspNet.DataProtection
{
    internal static class MockExtensions
    {
        /// <summary>
        /// Sets up a mock such that given the name of a deserializer class and the XML node that class's
        /// Import method should expect returns a descriptor which produces the given authenticator.
        /// </summary>
        public static void ReturnAuthenticatedEncryptorGivenDeserializerTypeNameAndInput(this Mock<IActivator> mockActivator, string typeName, string xml, IAuthenticatedEncryptor encryptor)
        {
            mockActivator
                .Setup(o => o.CreateInstance(typeof(IAuthenticatedEncryptorDescriptorDeserializer), typeName))
                .Returns(() =>
                {
                    var mockDeserializer = new Mock<IAuthenticatedEncryptorDescriptorDeserializer>();
                    mockDeserializer
                        .Setup(o => o.ImportFromXml(It.IsAny<XElement>()))
                        .Returns<XElement>(el =>
                        {
                            // Only return the descriptor if the XML matches
                            XmlAssert.Equal(xml, el);
                            var mockDescriptor = new Mock<IAuthenticatedEncryptorDescriptor>();
                            mockDescriptor.Setup(o => o.CreateEncryptorInstance()).Returns(encryptor);
                            return mockDescriptor.Object;
                        });
                    return mockDeserializer.Object;
                });
        }

        /// <summary>
        /// Sets up a mock such that given the name of a decryptor class and the XML node that class's
        /// Decrypt method should expect returns the specified XML elmeent.
        /// </summary>
        public static void ReturnDecryptedElementGivenDecryptorTypeNameAndInput(this Mock<IActivator> mockActivator, string typeName, string expectedInputXml, string outputXml)
        {
            mockActivator
                .Setup(o => o.CreateInstance(typeof(IXmlDecryptor), typeName))
                .Returns(() =>
                {
                    var mockDecryptor = new Mock<IXmlDecryptor>();
                    mockDecryptor
                        .Setup(o => o.Decrypt(It.IsAny<XElement>()))
                        .Returns<XElement>(el =>
                        {
                            // Only return the descriptor if the XML matches
                            XmlAssert.Equal(expectedInputXml, el);
                            return XElement.Parse(outputXml);
                        });
                    return mockDecryptor.Object;
                });
        }
    }
}
