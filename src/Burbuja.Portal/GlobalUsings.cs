// The Razor SDK's implicit usings stop at the BCL: a Razor Class Library is not a Web SDK
// project, so the ASP.NET Core namespaces it uses are imported here once.
global using Microsoft.AspNetCore.Builder;
global using Microsoft.AspNetCore.Http;
global using Microsoft.AspNetCore.Routing;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Logging;
