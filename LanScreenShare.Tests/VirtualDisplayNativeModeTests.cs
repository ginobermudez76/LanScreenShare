using System.Text.Json;
using LanScreenShare.Core.Display;

namespace LanScreenShare.Tests
{
    [TestClass]
    public class VirtualDisplayNativeModeTests
    {
        [TestMethod]
        public void SelectBestSupportedMode_PrefersNativeModeOverLargerAdvertisedMode()
        {
            var native = new VirtualDisplayMode(1920, 1080, 60);
            var modes = new[]
            {
                native,
                new VirtualDisplayMode(2560, 1440, 60),
                new VirtualDisplayMode(1600, 900, 60),
                new VirtualDisplayMode(1280, 720, 60)
            };

            var selected = VirtualDisplayModeValidator.SelectBestSupportedMode(modes, preferred: null, nativeMode: native);

            Assert.AreEqual(native, selected);
        }

        [TestMethod]
        public void SelectBestSupportedMode_PrefersNativeModeWithCustomRefreshRate()
        {
            var native = new VirtualDisplayMode(1366, 768, 75);
            var modes = new[]
            {
                native,
                new VirtualDisplayMode(1920, 1080, 60),
                new VirtualDisplayMode(2560, 1440, 60)
            };

            var selected = VirtualDisplayModeValidator.SelectBestSupportedMode(modes, preferred: null, nativeMode: native);

            Assert.AreEqual(native, selected);
        }

        [TestMethod]
        public void SelectBestSupportedMode_ExplicitPreferredOverridesNativeMode()
        {
            var native = new VirtualDisplayMode(1920, 1080, 60);
            var preferred = new VirtualDisplayMode(1600, 900, 60);
            var modes = new[]
            {
                native,
                new VirtualDisplayMode(2560, 1440, 60),
                preferred
            };

            var selected = VirtualDisplayModeValidator.SelectBestSupportedMode(modes, preferred: preferred, nativeMode: native);

            Assert.AreEqual(preferred, selected);
        }

        [TestMethod]
        public void SelectBestSupportedMode_WhenNativeModeIsNull_FallsBackToLargestValidMode()
        {
            var modes = new[]
            {
                new VirtualDisplayMode(1920, 1080, 60),
                new VirtualDisplayMode(2560, 1440, 60),
                new VirtualDisplayMode(1280, 720, 60)
            };

            var selected = VirtualDisplayModeValidator.SelectBestSupportedMode(modes, preferred: null, nativeMode: null);

            Assert.AreEqual(new VirtualDisplayMode(2560, 1440, 60), selected);
        }

        [TestMethod]
        public void SelectBestSupportedMode_WhenNativeModeIsInvalid_FallsBackToLargestValidMode()
        {
            var invalidNative = new VirtualDisplayMode(8000, 4500, 240);
            var modes = new[]
            {
                new VirtualDisplayMode(1920, 1080, 60),
                new VirtualDisplayMode(1600, 900, 60)
            };

            var selected = VirtualDisplayModeValidator.SelectBestSupportedMode(modes, preferred: null, nativeMode: invalidNative);

            Assert.AreEqual(new VirtualDisplayMode(1920, 1080, 60), selected);
        }

        [TestMethod]
        public void SelectBestSupportedMode_WhenSupportedModesEmptyButNativeValid_ReturnsNative()
        {
            var native = new VirtualDisplayMode(1920, 1080, 60);

            var selected = VirtualDisplayModeValidator.SelectBestSupportedMode([], preferred: null, nativeMode: native);

            Assert.AreEqual(native, selected);
        }

        [TestMethod]
        public void DisplayCapabilitiesDto_Serialization_RoundTripsNativeMode()
        {
            var dto = new DisplayCapabilitiesDto
            {
                DeviceName = "Receiver-PC",
                NativeMode = new VirtualDisplayMode(1920, 1080, 75),
                SupportedModes =
                [
                    new VirtualDisplayMode(1920, 1080, 75),
                    new VirtualDisplayMode(2560, 1440, 60)
                ],
                DpiScaleFactor = 1.25
            };

            string json = JsonSerializer.Serialize(dto);
            var deserialized = JsonSerializer.Deserialize<DisplayCapabilitiesDto>(json);

            Assert.IsNotNull(deserialized);
            Assert.AreEqual(dto.NativeMode, deserialized.NativeMode);
            Assert.AreEqual(2, deserialized.SupportedModes.Count);
        }

        [TestMethod]
        public void DisplayCapabilitiesDto_Deserialization_BackwardsCompatibleWithoutNativeMode()
        {
            const string legacyJson = """
                {
                  "SupportedModes": [
                    { "Width": 1920, "Height": 1080, "RefreshRateHz": 60 },
                    { "Width": 2560, "Height": 1440, "RefreshRateHz": 60 }
                  ],
                  "DpiScaleFactor": 1.0,
                  "OrientationDegrees": 0,
                  "DeviceName": "Legacy-Receiver",
                  "SupportsAuxiliaryInput": false,
                  "IsBusy": false
                }
                """;

            var deserialized = JsonSerializer.Deserialize<DisplayCapabilitiesDto>(legacyJson);

            Assert.IsNotNull(deserialized);
            Assert.IsNull(deserialized.NativeMode);
            Assert.AreEqual(2, deserialized.SupportedModes.Count);
        }
    }
}
