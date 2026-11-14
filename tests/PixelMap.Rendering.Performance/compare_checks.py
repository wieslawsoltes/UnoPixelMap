"""Contracts for the narrowly scoped visual transition and retained identity gates."""
import copy
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("native_compare", Path(__file__).with_name("compare.py"))
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)


def runs():
    metric = {"WallMilliseconds": 1, "ThreadCpuMilliseconds": 1, "AllocatedBytes": 0}
    sample = {key: "same" for key in driver.FRAME_IDENTITIES}
    sample.update(Index=0, Preparation=metric, Overlay=metric, Picking=metric)
    stream = {"Frame": 0, "TerrainPatches": 1, "TextureHash": "same", "PrepareMs": 1,
              "AllocatedBytes": 0, "RebakedTextures": 1, "RebakedBaseBytes": 4}
    header = dict(Runtime="fixture", Platform="fixture", Processors=1, Width=1200, Height=700, Warmup=32)
    result = []
    for candidate in (False, True, True, False):
        frames = {**header, "Font": "fixture", "Results": []}
        streaming = {**header, "Reports": []}
        for style, name in ((0, "Pixel"), (1, "Normal")):
            f, s = copy.deepcopy(sample), copy.deepcopy(stream)
            if candidate and name == "Pixel":
                f["ArtworkHash"] = s["TextureHash"] = "new-pixel-artwork"
            frames["Results"].append({"Name": name.lower() + "-pan", "Style": style, "Samples": [f]})
            streaming["Reports"].append({"Name": "stream-" + name.lower(), "RenderStyle": name, "Samples": [s]})
        result.append({"frames": frames, "streaming": streaming})
    return result


class TransitionChecks(unittest.TestCase):
    def setUp(self):
        self.transition = driver.artwork_policy("d014060af60b0b961b0b5e52f39359525da2435b")

    def test_exact_baseline_only(self):
        self.assertIsNotNone(self.transition)
        self.assertIsNone(driver.artwork_policy("0" * 40))
        self.assertFalse(driver.compare(runs())["passed"])

    def test_changed_pixel_artwork_is_retained(self):
        report = driver.compare(runs(), self.transition)
        self.assertTrue(report["passed"], report["identityFailures"])
        self.assertEqual(len(report["artworkComparisons"]), 4)
        self.assertTrue(all(item["changed"] for item in report["artworkComparisons"]))
        self.assertTrue(all(item["baseline"] == "same" and item["candidate"] == "new-pixel-artwork"
                            for item in report["artworkComparisons"]))

    def test_geometry_picking_and_other_styles_remain_exact(self):
        for style, field in ((0, "GeometryHash"), (0, "PickingHash"), (0, "OverlayHash"), (1, "ArtworkHash")):
            with self.subTest(style=style, field=field):
                values = runs()
                values[1]["frames"]["Results"][style]["Samples"][0][field] = "incorrect"
                self.assertFalse(driver.compare(values, self.transition)["passed"])

    def test_both_revisions_must_repeat_their_own_artwork(self):
        for kind, key, field in (("frames", "Results", "ArtworkHash"), ("streaming", "Reports", "TextureHash")):
            for position in (2, 3):
                with self.subTest(kind=kind, position=position):
                    values = runs()
                    values[position][kind][key][0]["Samples"][0][field] = "nondeterministic"
                    self.assertFalse(driver.compare(values, self.transition)["passed"])


if __name__ == "__main__":
    unittest.main()
