import { dotnet } from './_framework/dotnet.js';

if (typeof window === 'undefined') {
  throw new Error('The Workshop browser host must run in a browser.');
}

const runtime = await dotnet
  .withDiagnosticTracing(false)
  .withApplicationArgumentsFromQuery()
  .create();

const config = runtime.getConfig();
await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);
