using System.Runtime.InteropServices;

namespace PortPathProbe;

/// <summary>
/// Independent cfgmgr32 access for the ADR-0079 D4 cross-validation.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately a <b>second implementation</b>, not a call into
/// <c>Periphery.Windows.DevNodeHelper</c> (which is <c>internal</c> anyway). The whole
/// point of D4 is to compare the parser's reading of <c>LocationPath</c> against a walk
/// that shares no code with it. Reusing Periphery's helper would make the agreement
/// guaranteed and the measurement worthless.
/// </para>
/// <para>
/// Note the scope of what "independent" buys, per ADR-0079 D4: both this walk and the
/// string being parsed are projections of the same cfgmgr32 devnode tree. Agreement shows
/// the parser is faithful to that tree. It does not show the tree is faithful to the
/// machine — that is ADR-0079 D8's question, and its answer is "not on tunneled buses".
/// </para>
/// </remarks>
internal static unsafe partial class CfgMgr
{
    private const uint CR_SUCCESS = 0;
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    private const uint CR_BUFFER_SMALL = 0x0000001a;
    private const uint CR_NO_SUCH_VALUE = 0x00000025;
    private const uint CR_NO_SUCH_DEVNODE = 0x0000000d;

    // CM_DRP_* are 1-based ordinals into the legacy registry-property table.
    private const uint CM_DRP_DEVICEDESC = 0x01;
    private const uint CM_DRP_SERVICE    = 0x05;

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNode(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static partial uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static partial uint CM_Get_Device_ID(uint dnDevInst, char* buffer, uint bufferLen, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    private static partial uint CM_Get_DevNode_Registry_Property(
        uint dnDevInst, uint ulProperty, out uint pulRegDataType, void* buffer, ref uint pulLength, uint ulFlags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial uint CM_Get_DevNode_Property(
        uint dnDevInst, in DEVPROPKEY propertyKey, out uint propertyType, void* buffer, ref uint bufferSize, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    // DEVPKEY_Device_LocationPaths — {a45c254e-df1c-4efd-8020-67d146a850e0}, 37, STRING_LIST.
    private static readonly DEVPROPKEY DEVPKEY_Device_LocationPaths = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 37,
    };

    /// <summary>Locates a device instance id, or null when the node is not present.</summary>
    internal static uint? Locate(string instanceId)
        => CM_Locate_DevNode(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS
            ? devInst
            : null;

    /// <summary>Whether a parent lookup found one, found none, or failed.</summary>
    /// <remarks>
    /// The same distinction <see cref="PropertyRead"/> draws, for the same reason. A bare
    /// <c>uint?</c> reports a genuine top-of-tree and a cfgmgr32 failure identically, so a
    /// failed walk would be recorded as the instance-id fallback case — and the root-hub
    /// exception in the probe's pass condition would admit it.
    /// </remarks>
    internal enum ParentRead { NoParent = 0, Ok, Unreadable }

    internal static (ParentRead Status, uint DevInst) Parent(uint devInst)
        => CM_Get_Parent(out uint parent, devInst, 0) switch
        {
            CR_SUCCESS => (ParentRead.Ok, parent),
            CR_NO_SUCH_DEVNODE => (ParentRead.NoParent, 0u),
            _ => (ParentRead.Unreadable, 0u),
        };

    internal static string? InstanceId(uint devInst)
    {
        const int max = 512;
        char* buf = stackalloc char[max];
        return CM_Get_Device_ID(devInst, buf, max, 0) == CR_SUCCESS ? new string(buf) : null;
    }

    private static string? RegistryStringProperty(uint devInst, uint property)
    {
        uint len = 0;
        CM_Get_DevNode_Registry_Property(devInst, property, out _, null, ref len, 0);
        if (len == 0) return null;

        byte[] buf = new byte[len];
        fixed (byte* p = buf)
        {
            if (CM_Get_DevNode_Registry_Property(devInst, property, out _, p, ref len, 0) != CR_SUCCESS)
                return null;
            string s = new((char*)p);
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }

    internal static string? Service(uint devInst) => RegistryStringProperty(devInst, CM_DRP_SERVICE);

    internal static string? Description(uint devInst) => RegistryStringProperty(devInst, CM_DRP_DEVICEDESC);

    /// <summary>
    /// Whether a property read succeeded, and if so whether the property held anything.
    /// </summary>
    /// <remarks>
    /// The distinction is load-bearing for ADR-0079 D4. An earlier version of this file returned a
    /// bare empty array for both "the property is genuinely empty" and "the read failed", so an
    /// unreadable node was indistinguishable from a function node whose path
    /// <c>ResolveLocationPath</c> had synthesized — and the probe would have counted the former as
    /// evidence for the latter. That is the probe committing this ADR's own sin: reporting a
    /// missing measurement as a measured value.
    /// </remarks>
    internal enum PropertyRead
    {
        /// <summary>The node could not be located at all.</summary>
        NodeNotFound = 0,
        /// <summary>The read succeeded and the property held at least one non-empty string.</summary>
        Present,
        /// <summary>The read succeeded and the property is genuinely absent or empty.</summary>
        Absent,
        /// <summary>A cfgmgr32 call failed. NOT the same as absent.</summary>
        Unreadable,
    }

    /// <summary>
    /// The node's <b>own</b> <c>DEVPKEY_Device_LocationPaths</c> — the raw property, before
    /// <c>ResolveLocationPath</c>'s ancestor walk fills it in. Empty for function nodes, which is
    /// the Context claim this probe re-measures independently.
    /// </summary>
    internal static (PropertyRead Status, string[] Paths) OwnLocationPaths(uint devInst)
    {
        uint size = 0;
        uint sizeStatus = CM_Get_DevNode_Property(devInst, in DEVPKEY_Device_LocationPaths, out _, null, ref size, 0);

        // CR_BUFFER_SMALL (0x1a) is the expected result of a sizing call that found data.
        // CR_NO_SUCH_VALUE (0x25) means the property genuinely is not set. Anything else is a
        // failure we must not silently read as "absent".
        if (sizeStatus == CR_NO_SUCH_VALUE)
            return (PropertyRead.Absent, []);
        if (size == 0)
            return (sizeStatus is CR_SUCCESS or CR_BUFFER_SMALL ? PropertyRead.Absent : PropertyRead.Unreadable, []);
        if (sizeStatus != CR_BUFFER_SMALL && sizeStatus != CR_SUCCESS)
            return (PropertyRead.Unreadable, []);

        byte[] buf = new byte[size];
        fixed (byte* p = buf)
        {
            if (CM_Get_DevNode_Property(devInst, in DEVPKEY_Device_LocationPaths, out _, p, ref size, 0) != CR_SUCCESS)
                return (PropertyRead.Unreadable, []);

            // REG_MULTI_SZ-style: null-separated, double-null terminated.
            var result = new List<string>();
            char* cur = (char*)p;
            char* end = (char*)(p + size);
            while (cur < end && *cur != '\0')
            {
                string str = new(cur);
                result.Add(str);
                cur += str.Length + 1;
            }
            bool anyNonEmpty = result.Exists(static x => !string.IsNullOrEmpty(x));
            return (anyNonEmpty ? PropertyRead.Present : PropertyRead.Absent, [.. result]);
        }
    }

    /// <summary>Reads a node's own location paths by instance id, reporting lookup failure distinctly.</summary>
    internal static (PropertyRead Status, string[] Paths) OwnLocationPaths(string instanceId)
        => Locate(instanceId) is { } devInst ? OwnLocationPaths(devInst) : (PropertyRead.NodeNotFound, []);

    /// <summary>Why an ancestor walk stopped. Never conflated with "zero hubs" (ADR-0079 D7).</summary>
    internal enum Termination
    {
        ReachedRootHub,
        NoParent,
        LookupFailed,
        DepthExceeded,
        /// <summary>A CM_Get_Parent call failed. NOT the same as reaching the top of the tree.</summary>
        ParentUnreadable,
    }

    internal readonly record struct HubWalk(
        Termination Termination,
        int ExternalHubs,
        int Depth,
        string? RootHubId)
    {
        /// <summary>The count is only a count when the walk actually reached a root hub.</summary>
        public bool TryGetExternalHubCount(out int count)
        {
            count = ExternalHubs;
            return Termination == Termination.ReachedRootHub;
        }
    }

    /// <summary>
    /// Ground truth for ADR-0079 D4: walk <c>CM_Get_Parent</c> from <paramref name="instanceId"/>,
    /// counting external USB hubs, stopping at the root hub.
    /// </summary>
    /// <remarks>
    /// Hub-ness is decided by the driver <b>service</b> (<c>USBHUB</c> / <c>USBHUB3</c>), not by the
    /// device description. ADR-0078 describes the original walk as counting "nodes named as hubs";
    /// names are localized and vendor-editable, so this uses the service instead. A hub whose
    /// instance id begins <c>USB\ROOT_HUB</c> is the root hub and terminates the walk without
    /// being counted — that distinction is the whole of ADR-0079 D3/D4.
    /// </remarks>
    internal static HubWalk WalkExternalHubs(string instanceId, int maxDepth = 32)
    {
        if (Locate(instanceId) is not { } devInst)
            return new HubWalk(Termination.LookupFailed, 0, 0, null);

        int hubs = 0;
        uint current = devInst;
        for (int depth = 1; depth <= maxDepth; depth++)
        {
            var (parentStatus, parent) = Parent(current);
            if (parentStatus == ParentRead.NoParent)
                return new HubWalk(Termination.NoParent, hubs, depth, null);
            if (parentStatus == ParentRead.Unreadable)
                return new HubWalk(Termination.ParentUnreadable, hubs, depth, null);

            string? id = InstanceId(parent);
            if (id is null)
                return new HubWalk(Termination.LookupFailed, hubs, depth, null);

            if (id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase))
                return new HubWalk(Termination.ReachedRootHub, hubs, depth, id);

            string? service = Service(parent);
            if (service is not null
                && (service.Equals("USBHUB3", StringComparison.OrdinalIgnoreCase)
                 || service.Equals("USBHUB", StringComparison.OrdinalIgnoreCase)))
                hubs++;

            current = parent;
        }

        return new HubWalk(Termination.DepthExceeded, hubs, maxDepth, null);
    }

    /// <summary>How a resolve-style ancestor walk ended.</summary>
    internal enum ResolveOutcome
    {
        /// <summary>An ancestor (or the node itself) carried a location path.</summary>
        Found = 0,
        /// <summary>The chain ended with no ancestor carrying a path — the instance-id fallback case.</summary>
        NoAncestorCarriesPath,
        /// <summary>A cfgmgr32 read failed somewhere on the chain. NOT the same as "no path".</summary>
        Unreadable,
        /// <summary>The walk hit its bound.</summary>
        DepthExceeded,
    }

    /// <summary>
    /// How many ancestors <c>ResolveLocationPath</c> would have to walk before one carries a
    /// location path. Feeds the headroom question against its <c>maxDepth: 8</c> bound.
    /// </summary>
    internal static (int Hops, ResolveOutcome Outcome) DepthToFirstLocationPath(string instanceId, int maxDepth = 32)
    {
        if (Locate(instanceId) is not { } devInst)
            return (0, ResolveOutcome.Unreadable);

        var (ownStatus, _) = OwnLocationPaths(devInst);
        if (ownStatus == PropertyRead.Unreadable) return (0, ResolveOutcome.Unreadable);
        if (ownStatus == PropertyRead.Present) return (0, ResolveOutcome.Found);

        uint current = devInst;
        for (int depth = 1; depth <= maxDepth; depth++)
        {
            var (parentStatus, parent) = Parent(current);
            if (parentStatus == ParentRead.Unreadable)
                return (depth, ResolveOutcome.Unreadable);
            if (parentStatus == ParentRead.NoParent)
                return (depth, ResolveOutcome.NoAncestorCarriesPath);

            var (status, _) = OwnLocationPaths(parent);
            if (status == PropertyRead.Unreadable) return (depth, ResolveOutcome.Unreadable);
            if (status == PropertyRead.Present) return (depth, ResolveOutcome.Found);

            current = parent;
        }
        return (maxDepth, ResolveOutcome.DepthExceeded);
    }
}
