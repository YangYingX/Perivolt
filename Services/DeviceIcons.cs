using System.Windows.Media;

namespace PowerHub.Services;

public static class DeviceIcons
{
    // Original 24-unit outline icons. Shared frozen geometry avoids fonts and per-card parsing.
    private static readonly Geometry Keyboard = Freeze("M4,5 H20 Q22,5 22,7 V17 Q22,19 20,19 H4 Q2,19 2,17 V7 Q2,5 4,5 Z M5,9 H6 M9,9 H10 M13,9 H14 M17,9 H19 M5,12 H6 M9,12 H10 M13,12 H14 M17,12 H19 M5,15 H6 M9,15 H15 M18,15 H19");
    private static readonly Geometry Mouse = Freeze("M12,2 C8.7,2 6,4.7 6,8 V16 C6,19.3 8.7,22 12,22 C15.3,22 18,19.3 18,16 V8 C18,4.7 15.3,2 12,2 Z M12,3 V8 M6,10 H18");
    private static readonly Geometry Gamepad = Freeze("M7,7 H17 C19,7 20,9 20.5,12 L22,18 C22.5,20 20,21 18.5,19.5 L16,17 H8 L5.5,19.5 C4,21 1.5,20 2,18 L3.5,12 C4,9 5,7 7,7 Z M7,10 V14 M5,12 H9 M16,11 H16.02 M18,13 H18.02");
    private static readonly Geometry Headphones = Freeze("M4,12 V11 A8,8 0 0 1 20,11 V12 M4,12 H6 V20 H4 C2.9,20 2,19.1 2,18 V14 C2,12.9 2.9,12 4,12 Z M20,12 H18 V20 H20 C21.1,20 22,19.1 22,18 V14 C22,12.9 21.1,12 20,12 Z");
    private static readonly Geometry Microphone = Freeze("M12,2 A4,4 0 0 1 16,6 V11 A4,4 0 0 1 8,11 V6 A4,4 0 0 1 12,2 Z M5,10 V12 A7,7 0 0 0 19,12 V10 M12,19 V22 M8,22 H16");
    private static readonly Geometry Speaker = Freeze("M4,9 H7 L12,5 V19 L7,15 H4 Z M16,8 A6,6 0 0 1 16,16 M19,5 A10,10 0 0 1 19,19");
    private static readonly Geometry Device = Freeze("M6,3 H18 Q20,3 20,5 V19 Q20,21 18,21 H6 Q4,21 4,19 V5 Q4,3 6,3 Z M9,17 H15");

    public static Geometry Get(string category) => category switch
    {
        "键盘" => Keyboard, "鼠标" => Mouse, "手柄" => Gamepad,
        "耳机" => Headphones, "麦克风" => Microphone, "音频设备" => Speaker, _ => Device
    };

    private static Geometry Freeze(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
