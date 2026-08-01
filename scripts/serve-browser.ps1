[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Directory,

    [ValidateRange(1024, 65535)]
    [int]$Port = 5237
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Directory).Path)
$rootIndexExists = Test-Path -LiteralPath ([IO.Path]::Combine($root, 'index.html'))
$publishedIndexExists = Test-Path -LiteralPath ([IO.Path]::Combine($root, 'wwwroot', 'index.html'))
if (!$rootIndexExists -and $publishedIndexExists) {
    $root = [IO.Path]::Combine($root, 'wwwroot')
}
$rootPrefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$listener = [Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
Write-Host "Serving $root at http://127.0.0.1:$Port/"

$mimeTypes = @{
    '.html' = 'text/html; charset=utf-8'
    '.css'  = 'text/css; charset=utf-8'
    '.js'   = 'text/javascript; charset=utf-8'
    '.json' = 'application/json; charset=utf-8'
    '.wasm' = 'application/wasm'
    '.dll'  = 'application/octet-stream'
    '.dat'  = 'application/octet-stream'
    '.pdb'  = 'application/octet-stream'
    '.png'  = 'image/png'
    '.ico'  = 'image/x-icon'
}

try {
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        try {
            $relative = [Uri]::UnescapeDataString($context.Request.Url.AbsolutePath).TrimStart('/')
            if ([string]::IsNullOrWhiteSpace($relative)) {
                $relative = 'index.html'
            }

            $candidate = [IO.Path]::GetFullPath([IO.Path]::Combine($root, $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)))
            if (!$candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                !(Test-Path -LiteralPath $candidate -PathType Leaf)) {
                $context.Response.StatusCode = 404
                $payload = [Text.Encoding]::UTF8.GetBytes('Not found')
                $context.Response.OutputStream.Write($payload, 0, $payload.Length)
                continue
            }

            $extension = [IO.Path]::GetExtension($candidate).ToLowerInvariant()
            $context.Response.ContentType = $mimeTypes[$extension] ?? 'application/octet-stream'
            $context.Response.Headers['Cache-Control'] = 'no-store'
            $bytes = [IO.File]::ReadAllBytes($candidate)
            $context.Response.ContentLength64 = $bytes.Length
            $context.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        }
        catch {
            $context.Response.StatusCode = 500
            $payload = [Text.Encoding]::UTF8.GetBytes('Static host error')
            $context.Response.OutputStream.Write($payload, 0, $payload.Length)
        }
        finally {
            $context.Response.OutputStream.Close()
        }
    }
}
finally {
    $listener.Stop()
    $listener.Close()
}
