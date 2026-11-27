using Godot;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace STS2Mobile.DesktopHost.Harness;

/// <summary>
/// The room transforms behind the merchant that must not move when the inventory opens, takes stock or closes. The host
/// uses the native shop scenes; what it does with those transforms is a host partial (the launcher keeps the native
/// composition in LauncherShopChecks.cs).
/// </summary>
internal sealed partial class TestDriver
{
    private readonly record struct ShopFraming(Transform2D Art, Transform2D Party, Transform2D Merchant);

    private static ShopFraming ReadShopFraming(NMerchantButton merchant)
    {
        Control scene = merchant.GetParent<Control>();
        return new ShopFraming(scene.GetNode<Control>("BgContainer").GetGlobalTransform(),
            scene.GetNode<Control>("CharacterContainer").GetGlobalTransform(), merchant.GetGlobalTransform());
    }
}
