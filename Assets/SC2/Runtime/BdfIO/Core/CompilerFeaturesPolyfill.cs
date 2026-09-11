// CompilerFeaturesPolyfill.cs
//
// netstandard2.1 doesn't ship the marker types `init` properties and
// `required` members need (added in the .NET 5/7 BCL), but the C# 9-12
// compiler requires them to be present in the assembly being compiled.
// Standard polyfill (as in PolySharp): declare the same types ourselves,
// scoped to this assembly (internal). Doesn't affect the calling code's
// semantics/IL — `required` is still enforced by the compiler while
// building BdfIO.*; consumers (Unity) never see or use these attributes.

using System;

#if !NET7_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    internal sealed class CompilerFeatureRequiredAttribute : Attribute
    {
        public CompilerFeatureRequiredAttribute(string featureName) => FeatureName = featureName;
        public string FeatureName { get; }
        public bool IsOptional { get; set; }

        public const string RefStructs = nameof(RefStructs);
        public const string RequiredMembers = nameof(RequiredMembers);
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class RequiredMemberAttribute : Attribute { }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
    internal sealed class SetsRequiredMembersAttribute : Attribute { }
}
#endif
