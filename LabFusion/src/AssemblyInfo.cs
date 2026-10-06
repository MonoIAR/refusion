using System.Reflection;
using System.Runtime.CompilerServices;

using MelonLoader;

using LabFusion;

[assembly: InternalsVisibleTo("BonelabSupport")]

[assembly: AssemblyTitle(FusionMod.ModName)]
[assembly: AssemblyVersion(FusionVersion.VersionString)]
[assembly: AssemblyFileVersion(FusionVersion.VersionString)]

[assembly: MelonInfo(typeof(FusionMod), FusionMod.ModName, FusionVersion.VersionString, FusionMod.ModAuthor)]
[assembly: MelonGame(FusionMod.GameDeveloper)]
[assembly: MelonPriority(-10000)]