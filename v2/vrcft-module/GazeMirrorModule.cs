// VR Gaze Mirror 2.0 - the optional VRCFaceTracking module.
//
// VRCFT keeps one shared set of tracking data that every loaded module writes into; the real eye-tracking module
// (Pimax, SRanibro, EyeTrackVR, ALVR, ...) puts the gaze there. This module never writes into it: it only copies the
// eye part into a small shared-memory block the gaze mirror reads ("GazeMirror2.ExternalGaze"), so that the ring can
// follow the eyes in SteamVR games whose headset software does not feed SteamVR's own eye-tracking interface.
//
// VRCFT only keeps a module that says it provides eye or expression data, and a module that says "no eye" resets the
// eye status of the module before it. So this one claims both and must load LAST: VRCFT loads installed modules in the
// order of their folder names, which are the module IDs, and this module's ID starts with ffffffff (module.json).
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using VRCFaceTracking;
using VRCFaceTracking.Core.Params.Data;

namespace GazeMirror.VRCFT;

public class GazeMirrorModule : ExtTrackingModule
{
    // The block: see v2/protocol/gaze_mirror_protocol.h (ExternalGaze) - the offsets here are the C++ layout.
    private const string MappingName = "GazeMirror2.ExternalGaze";
    private const uint Magic = 0x58324D47; // 'GM2X'
    private const uint Version = 1;
    private const int Size = 400;
    private const int OffMagic = 0, OffVersion = 4, OffSize = 8, OffSequence = 16, OffWrittenMs = 24, OffWriterPid = 32, OffSource = 36;
    private const int OffLeftValid = 40, OffRightValid = 44, OffLeftX = 48, OffLeftY = 52, OffRightX = 56, OffRightY = 60;
    private const int OffLeftOpen = 64, OffRightOpen = 68, OffLeftPupil = 72, OffRightPupil = 76, OffWriterName = 80;
    private const int SourceVrcft = 1;

    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private long _sequence;
    private int _written;

    public override (bool SupportsEye, bool SupportsExpression) Supported => (true, true);

    public override (bool eyeSuccess, bool expressionSuccess) Initialize(bool eyeAvailable, bool expressionAvailable)
    {
        try
        {
            _mapping = MemoryMappedFile.CreateOrOpen(MappingName, Size);
            _view = _mapping.CreateViewAccessor(0, Size);
            if (_view.ReadUInt32(OffMagic) != Magic)
            {
                _view.Write(OffVersion, Version);
                _view.Write(OffSize, (uint)Size);
                _view.Write(OffMagic, Magic);
            }
            var name = System.Text.Encoding.UTF8.GetBytes("VRCFT module " + typeof(GazeMirrorModule).Assembly.GetName().Version?.ToString(3));
            var padded = new byte[32];
            Array.Copy(name, padded, Math.Min(name.Length, 31));
            _view.WriteArray(OffWriterName, padded, 0, padded.Length);
            _view.Write(OffWriterPid, Environment.ProcessId);
            _view.Write(OffSource, SourceVrcft);
        }
        catch (Exception e)
        {
            Logger?.LogError("Gaze Mirror: the shared block could not be opened: {error}", e.Message);
            Teardown();
            return (false, false);
        }
        if (eyeAvailable) Logger?.LogWarning("Gaze Mirror: no eye-tracking module was loaded before this one. It only passes eye data on; it makes none.");
        Logger?.LogInformation("Gaze Mirror: passing eye data on to the gaze mirror.");
        ModuleInformation.Name = "VR Gaze Mirror";
        // Both, always: see the note at the top. Nothing is written into VRCFT's data by this module.
        return (true, true);
    }

    public override void Update()
    {
        var view = _view;
        if (view != null)
        {
            ref var eye = ref UnifiedTracking.Data.Eye;
            var left = eye.Left;
            var right = eye.Right;
            // A sequence lock: odd while writing, so a reader that sees a change reads again.
            view.Write(OffSequence, ++_sequence);
            Thread.MemoryBarrier();
            view.Write(OffLeftValid, 1);
            view.Write(OffRightValid, 1);
            view.Write(OffLeftX, left.Gaze.x);
            view.Write(OffLeftY, left.Gaze.y);
            view.Write(OffRightX, right.Gaze.x);
            view.Write(OffRightY, right.Gaze.y);
            view.Write(OffLeftOpen, left.Openness);
            view.Write(OffRightOpen, right.Openness);
            view.Write(OffLeftPupil, left.PupilDiameter_MM);
            view.Write(OffRightPupil, right.PupilDiameter_MM);
            view.Write(OffWrittenMs, Environment.TickCount64);
            Thread.MemoryBarrier();
            view.Write(OffSequence, ++_sequence);
            if (++_written == 1) Logger?.LogInformation("Gaze Mirror: first eye sample passed on.");
        }
        // VRCFT calls this as fast as it can; the mirror needs no more than the headset makes.
        Thread.Sleep(8);
    }

    public override void Teardown()
    {
        try
        {
            if (_view != null)
            {
                _view.Write(OffLeftValid, 0);
                _view.Write(OffRightValid, 0);
                _view.Write(OffWriterPid, 0);
            }
        }
        catch { /* leaving anyway */ }
        _view?.Dispose();
        _mapping?.Dispose();
        _view = null;
        _mapping = null;
    }
}
