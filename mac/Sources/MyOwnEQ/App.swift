import SwiftUI

@main
struct MyOwnEQApp: App {
    @StateObject private var controller = AppController()

    var body: some Scene {
        MenuBarExtra {
            QuickPanel()
                .environmentObject(controller)
        } label: {
            Image(systemName: controller.state?.enabled == false ? "slider.vertical.3" : "slider.horizontal.3")
        }
        .menuBarExtraStyle(.window)

        Window("MyOwnEQ", id: "editor") {
            EditorWindow()
                .environmentObject(controller)
                .frame(minWidth: 900, minHeight: 620)
        }
        .defaultSize(width: 1100, height: 720)
    }
}
