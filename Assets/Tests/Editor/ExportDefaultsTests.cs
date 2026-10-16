using System.Collections.Generic;
using NUnit.Framework;
using Strands.Export;
using Strands.Sp16;

namespace Strands.Tests
{
    /// <summary>The Export page's choices kept from one song to the next.</summary>
    public class ExportDefaultsTests
    {
        // Layers on strips, in order: two on the first, one on the third.
        static List<ExportLayer> Layers(params int[] strips)
        {
            var layers = new List<ExportLayer>();
            foreach (int strip in strips)
                layers.Add(new ExportLayer { Label = "S" + layers.Count, Output = Sp16Output.Out12, Group = strip });
            return layers;
        }

        [Test]
        public void ExportDefaults_StartAsANewSongsOptionsDo()
        {
            var fresh = new WavExportOptions();
            var defaults = new ExportDefaults();
            Assert.That(defaults.Stretch, Is.EqualTo(fresh.Stretch));
            Assert.That(defaults.Arrangement, Is.EqualTo(fresh.Arrangement));
            Assert.That(defaults.TempoInArrangement, Is.EqualTo(fresh.TempoInArrangement));
            for (int place = 0; place < ExportDefaults.Places; place++)
                Assert.That(defaults.Choke(place) || defaults.Loop(place) || defaults.Velocity(place), Is.False);
        }

        [Test]
        public void ExportDefaults_AStripsSettingsGoToEveryTrackOnItAndOthersKeepTheirs()
        {
            var defaults = new ExportDefaults { Stretch = Sp16Stretch.Resample, Arrangement = false, TempoInArrangement = false };
            defaults.Set(0, true, false, true);
            defaults.Set(2, false, true, false);
            defaults.Set(3, true, true, true);

            List<ExportLayer> layers = Layers(0, 0, 2);
            var options = new WavExportOptions();
            defaults.ApplyTo(options, layers);
            Assert.That(options.Stretch, Is.EqualTo(Sp16Stretch.Resample));
            Assert.That(options.Arrangement, Is.False);
            Assert.That(options.TempoInArrangement, Is.False);
            for (int layer = 0; layer < 2; layer++)
            {
                Assert.That(options.Pads.ChokeOfLayer(layer), Is.True);
                Assert.That(options.Pads.LoopOfLayer(layer), Is.False);
                Assert.That(options.Pads.VelocityOfLayer(layer), Is.True);
            }
            Assert.That(options.Pads.LoopOfLayer(2), Is.True);
            Assert.That(options.Pads.ChokeOfLayer(2), Is.False);

            // Changed on the page and kept: the fourth strip, which this song does not use, keeps what it had.
            options.Pads.SetVelocityOfLayer(0, false);
            options.Pads.SetVelocityOfLayer(1, false);
            options.Stretch = Sp16Stretch.MasterTempo;
            defaults.TakeFrom(options, layers);
            Assert.That(defaults.Stretch, Is.EqualTo(Sp16Stretch.MasterTempo));
            Assert.That(defaults.Velocity(0), Is.False);
            Assert.That(defaults.Choke(0), Is.True);
            Assert.That(defaults.Choke(3) && defaults.Loop(3) && defaults.Velocity(3), Is.True);
        }

        [Test]
        public void ExportDefaults_ASongInOneFileHasAPlaceOfItsOwn()
        {
            var defaults = new ExportDefaults();
            var options = new WavExportOptions();
            options.Pads.SetChokeOfLayer(0, true);
            defaults.TakeFrom(options, new List<ExportLayer>());
            Assert.That(defaults.Choke(ExportDefaults.SongPlace), Is.True);
            Assert.That(defaults.Choke(0), Is.False);

            var next = new WavExportOptions();
            defaults.ApplyTo(next, null);
            Assert.That(next.Pads.ChokeOfLayer(0), Is.True);
        }

        [Test]
        public void ExportDefaults_AreKeptAsTextAndReadBack()
        {
            var defaults = new ExportDefaults { Stretch = Sp16Stretch.MasterTempo, Arrangement = false, TempoInArrangement = true };
            defaults.Set(1, true, false, false);
            defaults.Set(ExportDefaults.SongPlace, false, true, true);
            ExportDefaults read = ExportDefaults.FromText(defaults.ToText());
            Assert.That(read.ToText(), Is.EqualTo(defaults.ToText()));
            Assert.That(read.Stretch, Is.EqualTo(Sp16Stretch.MasterTempo));
            Assert.That(read.Arrangement, Is.False);
            Assert.That(read.Choke(1), Is.True);
            Assert.That(read.Velocity(ExportDefaults.SongPlace), Is.True);

            // Nothing kept yet, or something that cannot be read, gives the defaults.
            string fresh = new ExportDefaults().ToText();
            foreach (string bad in new[] { null, "", "nonsense", "Off;11;000", "Warp;11;000;000;000;000;000" })
                Assert.That(ExportDefaults.FromText(bad).ToText(), Is.EqualTo(fresh), bad ?? "null");
        }
    }
}
