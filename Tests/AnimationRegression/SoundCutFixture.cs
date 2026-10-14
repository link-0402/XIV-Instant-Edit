using System.Buffers.Binary;
using InstantEdit.Services.Animations.Timeline;
using InstantEdit.Services.Audio;
using InstantEdit.Ui.Timeline;

/// <summary>
/// Sounds across cuts: which spans of a sound's audio a cut takes out so it stays in step with the
/// motion, and the per-sound choice to cut it or leave it whole.
/// </summary>
internal static class SoundCutFixture
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        Spans(check);
        Audio(check, reject);
        Bake(check);
    }

    private static void Bake(Action<bool, string> check)
    {
        var template = AudioFixture.Template();
        var song = ScdBuilder.Build(template, new AudioClip(new float[22050 * 2], 22050, 1), 1);   // two seconds
        var doc = CutEditsFixture.Doc(100);
        doc = doc with { Events = doc.Events.SetItem(1, doc.Events[1] with { Path = "sound/test/song.scd" }) };      // t2 at 10
        var cut = CutEdits.RemoveRange(doc, 40, 60, out _);                                          // 20 frames go at output 40
        byte[]? Sound(string path) => path == "sound/test/song.scd" ? song : null;
        var chosen = cut with { SoundCuts = cut.SoundCuts.SetItem("t2", true) };
        var baked = TimelineSoundEdits.BakeCuts(TimelineSoundEdits.Bake(chosen, Sound, _ => null, _ => TimelineSoundRoute.Copy), Sound, template, _ => 2);
        var copyPath = CutEditsFixture.Find(baked.Document, "t2")!.Path;
        check(copyPath.StartsWith("sound/instant_edit/song_cut_", StringComparison.Ordinal) && baked.Document.SoundCuts.IsEmpty &&
              baked.Copies.Single(c => c.GamePath == copyPath).Files[copyPath] is var bytes && Math.Abs(ScdFile.TrySeconds(bytes)!.Value - (2 - 20 / 30.0)) < .01,
            "a sound cut with the motion is saved as a copy without the removed frames");
        var whole = cut with { SoundCuts = cut.SoundCuts.SetItem("t2", false) };
        var kept = TimelineSoundEdits.BakeCuts(TimelineSoundEdits.Bake(whole, Sound, _ => null, _ => TimelineSoundRoute.Copy), Sound, template, _ => 2);
        check(CutEditsFixture.Find(kept.Document, "t2")!.Path == "sound/test/song.scd" && kept.Copies.IsEmpty, "a sound left whole keeps its file");
    }

    private static void Audio(Action<bool, string> check, Action<Action, string> reject)
    {
        var pcm = AudioFixture.Template();                          // an MS-ADPCM one-shot
        var clip = ScdAudioCut.Decode(ScdModel.Read(pcm).Audio[0]);
        var cutClip = ScdAudioCut.Cut(clip, [new TimelineAudioSpan(clip.Seconds / 4, clip.Seconds / 4)]);
        check(clip.Seconds > 0 && Math.Abs(cutClip.Seconds - clip.Seconds * 3 / 4) < 0.002, "a span leaves its length out");
        check(Math.Abs(ScdAudioCut.Cut(clip, [new TimelineAudioSpan(clip.Seconds / 2, double.PositiveInfinity)]).Seconds - clip.Seconds / 2) < 0.002, "a tail cut");
        var written = ScdAudioCut.Apply(pcm, 0, [new TimelineAudioSpan(clip.Seconds / 4, clip.Seconds / 4)], pcm);
        check(ScdModel.Read(written).Sounds[0].Volume == ScdModel.Read(pcm).Sounds[0].Volume &&
              Math.Abs(ScdModel.Read(written).Audio[^1].Seconds - clip.Seconds * 3 / 4) < 0.01, "the cut copy keeps the sound's settings");
        var ogg = AudioFixture.SilentVorbis(2, 48000, 20);
        check(ScdAudioCut.Decode(ScdModel.Read(VorbisScd(ogg, 0x2003)).Audio[0]).Seconds > 0 &&
              ScdAudioCut.Decode(ScdModel.Read(VorbisScd(ogg, 0x2002)).Audio[0]).Seconds > 0, "XOR-table and XOR-byte Vorbis decode");
        reject(() => ScdAudioCut.Apply(LoopedTemplate(), 0, [new TimelineAudioSpan(0.001, 0.001)], pcm), "a sound that loops in its file is refused");
        var hca = ScdModel.Read(pcm);
        BinaryPrimitives.WriteInt32LittleEndian(hca.Audio[0].Header.AsSpan(12), (int)ScdCodec.Hca);
        reject(() => ScdAudioCut.Decode(hca.Audio[0]), "a format that can't be cut is refused");
    }

    /// <summary>The MS-ADPCM template with its audio looping from start to end, as a file with loop points has it.</summary>
    private static byte[] LoopedTemplate()
    {
        var model = ScdModel.Read(AudioFixture.Template());
        model.Audio[0].LoopEnd = 256;
        return model.Write();
    }

    /// <summary>
    /// The template with its audio swapped for <paramref name="ogg"/> as the game stores Vorbis: the Ogg
    /// header pages in the sub-info, the audio pages as data, XORed by <paramref name="mode"/> (0x2002:
    /// the headers with one byte; 0x2003: headers and data with the table).
    /// </summary>
    private static byte[] VorbisScd(byte[] ogg, short mode)
    {
        // The third page holds the audio: SilentVorbis writes identification, comment and setup, then audio.
        var pages = Enumerable.Range(0, ogg.Length - 3).Where(i => ogg[i] == 'O' && ogg[i + 1] == 'g' && ogg[i + 2] == 'g' && ogg[i + 3] == 'S').ToArray();
        var header = ogg[..pages[2]];
        var data = ogg[pages[2]..];
        const byte encodeByte = 0x5A;
        if (mode == 0x2002)
            for (var i = 0; i < header.Length; i++)
                header[i] ^= encodeByte;
        else
        {
            byte[] both = [.. header, .. data];
            var key = data.Length & 0x7F;
            for (var i = 0; i < both.Length; i++)
                both[i] ^= (byte)(ScdFile.VorbisXor[((key & 0x3F) + i) & 0xFF] ^ key);
            (header, data) = (both[..header.Length], both[header.Length..]);
        }
        var info = new byte[0x20];
        BinaryPrimitives.WriteInt16LittleEndian(info, mode);
        BinaryPrimitives.WriteInt16LittleEndian(info.AsSpan(2), encodeByte);
        BinaryPrimitives.WriteInt32LittleEndian(info.AsSpan(20), header.Length);
        var model = ScdModel.Read(AudioFixture.Template());
        var audio = model.Audio[0];
        BinaryPrimitives.WriteInt32LittleEndian(audio.Header.AsSpan(0), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(audio.Header.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(audio.Header.AsSpan(8), 48000);
        BinaryPrimitives.WriteInt32LittleEndian(audio.Header.AsSpan(12), (int)ScdCodec.Vorbis);
        BinaryPrimitives.WriteInt32LittleEndian(audio.Header.AsSpan(24), info.Length + header.Length);
        audio.SubInfo = [.. info, .. header];
        audio.Data = data;
        return model.Write();
    }

    private static void Spans(Action<bool, string> check)
    {
        var d = CutEdits.RemoveRange(CutEditsFixture.Doc(100), 40, 60, out _);                    // join at 40, 20 frames removed
        check(TimelineSoundCuts.Spans(d, 0, 10).SequenceEqual([new TimelineAudioSpan(40 / 30.0, 20 / 30.0)]), "a song from frame 0 loses the removed 20 frames at 1.33 s");
        check(TimelineSoundCuts.Spans(d, 50, 10).IsEmpty && TimelineSoundCuts.Spans(d, 0, 1).IsEmpty, "sounds after the cut or ending before it don't cross");
        var trimmed = CutEdits.RemoveRange(CutEditsFixture.Doc(100), 88, 100, out _);
        check(TimelineSoundCuts.Spans(trimmed, 0, 3.4).SequenceEqual([new TimelineAudioSpan(88 / 30.0, double.PositiveInfinity)]), "a trimmed end cuts the tail");
        check(TimelineSoundCuts.Spans(trimmed, 0, 2.9).IsEmpty, "a sound ending before the new end is whole");
        // Two cuts: the second span starts where the audio is once the first is gone, which is the output's time.
        var twice = CutEdits.RemoveRange(d, 60, 70, out _);                                         // output 60-70 = source 80-90
        check(TimelineSoundCuts.Spans(twice, 0, 10).SequenceEqual([new TimelineAudioSpan(40 / 30.0, 20 / 30.0), new TimelineAudioSpan(60 / 30.0, 10 / 30.0)]),
            "each span starts in the audio as the spans before it left it");
        var song = CutEditsFixture.Find(d, "t2")!;                                                 // a sound at 10
        check(TimelineSoundCuts.Crosses(d, song, 2) && !TimelineSoundCuts.Crosses(d, song, .5), "a sound crosses a cut when it plays past it");

        var editor = new TimelineEditor(d);
        check(editor.SetSoundCut("t2", true) && editor.Document.SoundCuts["t2"] && editor.SetSoundCut("t2", false) && !editor.Document.SoundCuts["t2"] &&
              editor.SetSoundCut("t2", null) && !editor.Document.SoundCuts.ContainsKey("t2") && !editor.SetSoundCut("t2", null) &&
              editor.Undo() && !editor.Document.SoundCuts["t2"], "the choice per sound is one undo step each, and clearing it asks again");
        check(CutEditsFixture.Doc(100).SoundCuts.IsEmpty && (CutEditsFixture.Doc(100) with { SoundCuts = null! }).SoundCuts.IsEmpty,
            "a document starts with no choices");
    }
}
