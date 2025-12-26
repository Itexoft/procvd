// Copyright (c) 2011-2026 Denis Kudelin
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.
// This Source Code Form is "Incompatible With Secondary Licenses", as defined by the Mozilla Public License, v. 2.0.

using System.Reflection;
using Procvd.Runtime;

namespace Procvd.Tests;

public class ProgramTests
{
    [Test]
    public void TryAcquireInstanceLock_PreventsSecondAcquireForSameExecutablePath()
    {
        var programType = typeof(ProcessExecutionRequest).Assembly.GetType("Procvd.Program", throwOnError: true)!;
        var acquireMethod = programType.GetMethod("TryAcquireInstanceLock", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] firstCall = [null, null];
        object?[] secondCall = [null, null];
        object?[] thirdCall = [null, null];

        try
        {
            Assert.That((bool)acquireMethod.Invoke(null, firstCall)!, Is.True);
            Assert.That(firstCall[0], Is.TypeOf<FileStream>());
            Assert.That(firstCall[1], Is.Null);

            Assert.That((bool)acquireMethod.Invoke(null, secondCall)!, Is.False);
            Assert.That(secondCall[0], Is.Null);
            Assert.That((string?)secondCall[1], Does.Contain("already running"));
        }
        finally
        {
            (firstCall[0] as FileStream)?.Dispose();
        }

        Assert.That((bool)acquireMethod.Invoke(null, thirdCall)!, Is.True);
        Assert.That(thirdCall[0], Is.TypeOf<FileStream>());

        (thirdCall[0] as FileStream)?.Dispose();
    }
}
