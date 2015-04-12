// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#if !MONO
// we only ever p/invoke into DLLs known to be in the System32 folder
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
#endif

[assembly: InternalsVisibleTo("Microsoft.AspNet.Cryptography.Internal.Test")]
[assembly: InternalsVisibleTo("Microsoft.AspNet.Cryptography.KeyDerivation")]
[assembly: InternalsVisibleTo("Microsoft.AspNet.Cryptography.KeyDerivation.Test")]
[assembly: InternalsVisibleTo("Microsoft.AspNet.DataProtection")]
[assembly: InternalsVisibleTo("Microsoft.AspNet.DataProtection.Interfaces.Test")]
[assembly: InternalsVisibleTo("Microsoft.AspNet.DataProtection.Test")]
[assembly: AssemblyMetadata("Serviceable", "True")]
