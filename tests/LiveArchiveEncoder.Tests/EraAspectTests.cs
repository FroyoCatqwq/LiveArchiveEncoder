using LiveArchiveEncoder.PostProcess;

namespace LiveArchiveEncoder.Tests;

/// <summary>
/// v7.9.6.3: era presets can be captured and delivered in 4:3 or 16:9, and the
/// switch is only allowed where the selected encoder can actually encode the
/// resulting frame.
///
/// The failure this prevents is not cosmetic: asking h263 for 320x180, or amv
/// for 176x132, does not fail at argument-parse time -- FFmpeg accepts the
/// command line and dies on the first frame, minutes into a realtime-paced run.
/// </summary>
public sealed class EraAspectTests
{
    [Test]
    public async Task ParsesEverySpellingTheCliAccepts()
    {
        (string Text, EraAspect Expected)[] cases =
        [
            ("4:3", EraAspect.FourThree), ("4/3", EraAspect.FourThree), ("43", EraAspect.FourThree),
            ("16:9", EraAspect.SixteenNine), ("16/9", EraAspect.SixteenNine),
            ("source", EraAspect.Source), ("native", EraAspect.Source),
        ];

        foreach ((string text, EraAspect expected) in cases)
        {
            await Assert.That(EraAspects.TryParse(text, out EraAspect parsed)).IsTrue().Because(text);
            await Assert.That(parsed).IsEqualTo(expected).Because(text);
        }

        // An absent flag arrives as null, which must mean "no switch", and a
        // blank value is a typo worth refusing rather than guessing at.
        await Assert.That(EraAspects.TryParse(null, out _)).IsFalse();
        await Assert.That(EraAspects.TryParse("", out _)).IsFalse();
        await Assert.That(EraAspects.TryParse("21:9", out _)).IsFalse();
    }

    [Test]
    public async Task KeepsTheCaptureWidthAndChangesTheHeight()
    {
        // youku07 is 352x264 (4:3). Widescreen keeps the width the preset was
        // tuned for, so 352x198 -- same horizontal detail, fewer rows.
        EraPreset youku = EraPresets.Find("youku07")!;
        (int W, int H)? wide = EraAspects.Resolve(youku, EraAspect.SixteenNine, "flv", out string? reason);
        await Assert.That(reason).IsNull();
        await Assert.That(wide).IsEqualTo((352, 198));

        // The other direction: hd14 is 1280x720, and 4:3 at that width is 960
        // rows. libx264 has no size limits that matter here.
        EraPreset hd = EraPresets.Find("hd14")!;
        (int W, int H)? tall = EraAspects.Resolve(hd, EraAspect.FourThree, "libx264", out string? reason2);
        await Assert.That(reason2).IsNull();
        await Assert.That(tall).IsEqualTo((1280, 960));
    }

    [Test]
    public async Task SourceShapeIsThePresetItself()
    {
        EraPreset cctv = EraPresets.Find("cctv06")!;               // 384x288
        foreach (EraAspect aspect in new[] { EraAspect.Source, EraAspect.FourThree })
        {
            (int W, int H)? size = EraAspects.Resolve(cctv, aspect, "flv", out string? reason);
            await Assert.That(reason).IsNull().Because(aspect.ToString());
            await Assert.That(size).IsEqualTo((384, 288)).Because(aspect.ToString());
        }
    }

    [Test]
    public async Task CifEncodersRefuseAShapeTheyDoNotHave()
    {
        // H.261/H.263 exist at CIF sizes only, so a 16:9 request has no answer.
        // Returning the nearest even pair instead would be a lie FFmpeg then
        // refuses on the first frame.
        foreach (string encoder in new[] { "h261", "h263" })
        {
            EraPreset preset = EraPresets.Find("flv07")!;
            (int W, int H)? size = EraAspects.Resolve(preset, EraAspect.SixteenNine, encoder, out string? reason);
            await Assert.That(size).IsNull().Because(encoder);
            await Assert.That(reason).IsNotNull().Because(encoder);
            await Assert.That(reason!).Contains("only exists at").Because(encoder);
        }

        // 4:3 on the same preset has no CIF answer either -- 352x264 is 4:3 but
        // not a CIF size, so it is refused with the list rather than snapped.
        EraPreset flv = EraPresets.Find("flv07")!;
        await Assert.That(EraAspects.Resolve(flv, EraAspect.FourThree, "h263", out string? why)).IsNull();
        await Assert.That(why!).Contains("only exists at");

        // A CIF size itself passes, which is what the refusal is protecting.
        await Assert.That(EraAspects.Support("h263", 352, 288)).IsNull();
        await Assert.That(EraAspects.Support("h261", 352, 288)).IsNull();
    }

    [Test]
    public async Task AmvStillNeedsMultiplesOfSixteen()
    {
        // 320x180 is even but not a multiple of 16; AMV warns and writes a file
        // most hardware decoders reject, so it is refused up front.
        await Assert.That(EraAspects.Support("amv", 320, 180)).IsNotNull();
        await Assert.That(EraAspects.Support("amv", 320, 176)).IsNull();
    }

    [Test]
    public async Task EveryBuiltInPresetIsEitherSupportedOrExplained()
    {
        // The invariant behind the menu: whatever Resolve hands back has to be a
        // frame the encoder accepts, and a refusal has to carry a reason. A
        // silent null would leave the caller with nothing to print.
        string[] encoders = ["rv20", "flv", "libx264", "wmv2", "h263", "mpeg4"];
        foreach (EraPreset preset in EraPresets.WithLiveLayer)
        {
            foreach (EraAspect aspect in new[] { EraAspect.FourThree, EraAspect.SixteenNine })
            {
                foreach (string encoder in encoders)
                {
                    (int W, int H)? size = EraAspects.Resolve(preset, aspect, encoder, out string? reason);
                    if (size is null)
                    {
                        await Assert.That(reason).IsNotNull()
                            .Because($"{preset.Id}/{aspect}/{encoder} refused without a reason");
                        continue;
                    }
                    await Assert.That(EraAspects.Support(encoder, size.Value.W, size.Value.H)).IsNull()
                        .Because($"{preset.Id}/{aspect}/{encoder} -> {size.Value.W}x{size.Value.H}");
                }
            }
        }
    }

    [Test]
    public async Task TheWindowsMediaPresetsTakeBothShapes()
    {
        // 320x240 -> 320x180 for wmv2, and 176x144 (QCIF) -> 176x100, both even.
        EraPreset cctv = EraPresets.Find("cctv03")!;
        (int W, int H)? wide = EraAspects.Resolve(cctv, EraAspect.SixteenNine, "wmv2", out string? reason);
        await Assert.That(reason).IsNull();
        await Assert.That(wide).IsEqualTo((320, 180));

        EraPreset netease = EraPresets.Find("netease03")!;
        (int W, int H)? qcif = EraAspects.Resolve(netease, EraAspect.SixteenNine, "wmv2", out string? reason2);
        await Assert.That(reason2).IsNull();
        await Assert.That(qcif!.Value.W % 2).IsEqualTo(0);
        await Assert.That(qcif.Value.H % 2).IsEqualTo(0);
    }

    [Test]
    public async Task TheSecondPassDeliversTheSameShapeAsTheCapture()
    {
        // A preset at another shape whose second pass still wrote the old size
        // would describe two different recordings; the fragment is what the
        // recompress stage actually runs.
        EraPreset youku = EraPresets.Find("youku07")!;
        string native = youku.RecompressArgsFor(native: true, (320, 180));
        await Assert.That(native).Contains("scale=320:180");

        // The default (no switch) is unchanged, which is what every version
        // before this one produced.
        await Assert.That(youku.RecompressArgs).Contains($"scale={youku.RcWidth}:{youku.RcHeight}");
        await Assert.That(youku.Summary()).Contains($"{youku.RcWidth}x{youku.RcHeight}");
        await Assert.That(youku.Summary((320, 180))).Contains("320x180");
    }
}
