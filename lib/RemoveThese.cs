// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

// This file contains stubs to things that are referenced but not actually needed.
// These should be fixed in the shared code, e.g. by moving out the parts that
// reference these into a separate source file.

using Common.MT.Segments;
using System.Collections.Generic;

// @TODO: find out how to install this correctly in dotnet 2.x and 3.x. Then delete this.
namespace Microsoft.VisualStudio.TestTools.UnitTesting
{
    public static class Assert
    {
        public static void IsTrue(bool condition) { System.Diagnostics.Debug.Assert(condition); }
        public static void IsFalse(bool condition) { IsTrue(!condition); }
        public static void AreEqual<T>(T a, T b) { IsTrue(a.Equals(b)); } // @TODO: correct?
    }
}
namespace Microsoft.VisualStudio.TestTools.UnitTesting
{
    using System;

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public sealed class TestClassAttribute : System.Attribute
    {
        public TestClassAttribute() { }
    }
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Event, Inherited = false, AllowMultiple = false)]
    public sealed class TestMethodAttribute : Attribute
    {
        public TestMethodAttribute() { }
    }
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class DeploymentItemAttribute : Attribute
    {
        public DeploymentItemAttribute(string path, string outputDirectory) { }
    }
}
