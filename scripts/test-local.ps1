$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$base = "http://localhost:5100"

function Send-Json($method, $path, $body, $token = $null) {
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method), "$base$path")
    if ($null -ne $body) {
        $json = ConvertTo-Json -InputObject $body -Depth 10 -Compress
        $request.Content = [System.Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, "application/json")
    }
    if ($token) {
        $request.Headers.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new("Bearer", $token)
    }
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    return [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $text }
}

$catalog = Send-Json "GET" "/catalog/v1/products" $null
if ($catalog.Status -ne 200) { throw "Catalog browse returned HTTP $($catalog.Status)." }
$productId = (($catalog.Body | ConvertFrom-Json)[0]).id
$email = "smoke-$([guid]::NewGuid().ToString('N'))@example.test"
$password = "LocalTest!2026Password"
$registered = Send-Json "POST" "/identity/v1/register" @{ email = $email; password = $password }
if ($registered.Status -ne 201) { throw "Registration returned HTTP $($registered.Status)." }
$login = Send-Json "POST" "/identity/v1/login" @{ email = $email; password = $password }
if ($login.Status -ne 200) { throw "Login returned HTTP $($login.Status)." }
$token = ($login.Body | ConvertFrom-Json).accessToken

$fault = Send-Json "POST" "/catalog/v1/chaos/fail-next/1" $null
if ($fault.Status -ne 200) { throw "Development fault injector returned HTTP $($fault.Status)." }
$successful = Send-Json "POST" "/orders/v1/orders" @{
    productId = $productId; quantity = 1; simulatePaymentFailure = $false
} $token
$successfulOrder = $successful.Body | ConvertFrom-Json
if ($successful.Status -ne 201 -or $successfulOrder.status -ne "Confirmed") {
    throw "Successful order failed: HTTP $($successful.Status), status $($successfulOrder.status)."
}

$stockBefore = (Send-Json "GET" "/inventory/v1/stock/$productId" $null).Body | ConvertFrom-Json
$cancelled = Send-Json "POST" "/orders/v1/orders" @{
    productId = $productId; quantity = 1; simulatePaymentFailure = $true
} $token
$cancelledOrder = $cancelled.Body | ConvertFrom-Json
$stockAfter = (Send-Json "GET" "/inventory/v1/stock/$productId" $null).Body | ConvertFrom-Json
if ($cancelled.Status -ne 409 -or $cancelledOrder.status -ne "Cancelled" -or
    $stockBefore.onHand -ne $stockAfter.onHand -or $stockAfter.reserved -ne 0) {
    throw "Compensation failed: HTTP $($cancelled.Status), status $($cancelledOrder.status)."
}

$anonymousAdmin = Send-Json "POST" "/inventory/v1/stock/$productId/adjust" @{ delta = 1 }
$customerAdmin = Send-Json "POST" "/inventory/v1/stock/$productId/adjust" @{ delta = 1 } $token
$anonymousPayment = Send-Json "POST" "/payments/v1/charges" @{
    orderId = [guid]::NewGuid(); amount = 1; simulateFailure = $false
}
$customerPayment = Send-Json "POST" "/payments/v1/charges" @{
    orderId = [guid]::NewGuid(); amount = 1; simulateFailure = $false
} $token
if ($anonymousAdmin.Status -ne 401 -or $customerAdmin.Status -ne 403 -or
    $anonymousPayment.Status -ne 401 -or $customerPayment.Status -ne 403) {
    throw "Expected Admin endpoint statuses 401/403; got inventory $($anonymousAdmin.Status)/$($customerAdmin.Status), payments $($anonymousPayment.Status)/$($customerPayment.Status)."
}

Write-Output "PASS: public browse and registration/login"
Write-Output "PASS: transient catalog fault retried; order confirmed"
Write-Output "PASS: payment-failure compensation"
Write-Output "PASS: anonymous/Admin authorization responses 401/403 for inventory and payments"
$client.Dispose()
