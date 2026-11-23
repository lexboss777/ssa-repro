using Foundation;
using UIKit;

[Register("SceneDelegate")]
public class SceneDelegate : UIWindowSceneDelegate
{
	UIWindow window;

	public override void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
	{
		if (scene is UIWindowScene windowScene)
		{
			window = new UIWindow(windowScene);
			window.RootViewController = new UIViewController();
			window.MakeKeyAndVisible();
		}

		// Kick off the repro workload as soon as the app is up — no user interaction.
		Spin.Start();
	}
}
