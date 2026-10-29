// The app's WebAssembly entry point. Stomme's client starts the app: it registers the framework's services,
// loads the modules the server streams to the browser, and runs the host. This app's own client components are in this
// assembly, which Stomme's client finds as it finds a module's.
await Stomme.Client.Program.Main(args);
