param([string]$BaseUrl = 'http://localhost:8080')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
New-Item -ItemType Directory -Force evidence | Out-Null
Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(15)
$createdIds = [System.Collections.Generic.List[int]]::new()

Start-Transcript -Path ("evidence/verification-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
function Compose {
    $ErrorActionPreference = 'Continue'
    Write-Host "COMMAND: docker compose $args"
    & docker compose @args 2>&1 | ForEach-Object { Write-Host $_.ToString() }
    if ($LASTEXITCODE -ne 0) { throw "docker compose failed: $args" }
}
function Request([string]$Method, [string]$Path, $Body, [int]$Expected) {
    $message = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), "$BaseUrl$Path")
    if ($null -ne $Body) {
        $json = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Compress }
        $message.Content = [System.Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json')
    }
    $response = $null
    try {
        $response = $client.SendAsync($message).GetAwaiter().GetResult()
        $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $status = [int]$response.StatusCode
        Write-Host "$Method $Path -> $status (expected $Expected)"
        Write-Host $content
        if ($status -ne $Expected) { throw "Unexpected HTTP status $status" }
        if ($status -eq 201) {
            $item = $content | ConvertFrom-Json
            if ($response.Headers.Location.OriginalString -ne "/events/$($item.id)") { throw 'Invalid Location header' }
            Write-Host "Location: $($response.Headers.Location)"
        }
        if ($Expected -eq 204 -and $content.Length -ne 0) { throw '204 response must be empty' }
        if ($content -and $status -lt 400) { return ($content | ConvertFrom-Json) }
    } finally {
        if ($response) { $response.Dispose() }
        $message.Dispose()
    }
}
function Wait-Api {
    for ($i = 0; $i -lt 60; $i++) {
        try {
            $health = Request GET /health $null 200
            if ($health.status -eq 'ok') { return }
        } catch { }
        Start-Sleep -Seconds 2
    }
    throw 'API did not become ready.'
}
function DatabaseSnapshot([int]$EventId) {
    $ErrorActionPreference = 'Continue'
    Write-Host "COMMAND: PostgreSQL SELECT row_to_json(events) WHERE id=$EventId (via stdin)"
    $rows = @("SELECT row_to_json(e) FROM events e WHERE id=$EventId;" | & docker compose exec -T db sh -c 'exec psql -U $POSTGRES_USER -d $POSTGRES_DB -At -v ON_ERROR_STOP=1' 2>&1)
    $code = $LASTEXITCODE
    foreach ($row in $rows) { Write-Host $row.ToString() }
    if ($code -ne 0 -or $rows.Count -ne 1) { throw 'PostgreSQL snapshot failed' }
    return ($rows[0].ToString() | ConvertFrom-Json)
}
function Assert-Fields($item, $expected) {
    foreach ($field in @('title','date','location','description')) {
        if ($item.$field -cne $expected.$field) { throw "Mismatch: $field" }
    }
}
try {
    Write-Host "Actual test run: $(Get-Date -Format o)"
    Compose version
    Compose config --quiet
    Compose up --build -d
    Wait-Api
    Compose ps
    $body = @{ title = "Compose тест $([Guid]::NewGuid())"; date = '2026-10-15'; location = 'Казань'; description = 'Проверка PostgreSQL и volume' }
    $created = Request POST /events $body 201
    $id = [int]$created.id
    if ($id -le 0) { throw 'Missing event id' }
    $createdIds.Add($id)
    Assert-Fields $created $body
    $read = Request GET "/events/$id" $null 200
    Assert-Fields $read $body
    $list = @(Request GET /events $null 200)
    if ($id -notin $list.id) { throw 'Created event missing in list' }
    # Кавычки и SQL-подобная строка должны сохраниться буквально, без выполнения SQL.
    $body.title = "Обновлено: O'Reilly; DROP TABLE events; --"
    $body.date = '2026-10-16'
    $body.location = 'Москва'
    $body.description = 'Изменены все поля'
    $updated = Request PUT "/events/$id" $body 200
    Assert-Fields $updated $body
    Request POST /events @{ title = ''; date = '2026-10-15'; location = 'X' } 400
    Request POST /events @{ title = 'Missing date'; location = 'X' } 400
    Request POST /events @{ title = 'Invalid date'; date = 'wrong'; location = 'X' } 400
    Request POST /events @{ title = 'Invalid day'; date = '2026-02-30'; location = 'X' } 400
    Request POST /events @{ title = ('X' * 201); date = '2026-10-15'; location = 'X' } 400
    Request POST /events @{ title = 'X'; date = '2026-10-15'; location = ('X' * 301) } 400
    Request POST /events @{ title = 'X'; date = '2026-10-15'; location = 'X'; description = ('X' * 2001) } 400
    Request POST /events '{broken json' 400
    Request PUT "/events/$id" @{ title = ''; date = '2026-10-15'; location = 'X' } 400
    Assert-Fields (Request GET "/events/$id" $null 200) $body
    $optional = Request POST /events @{ title = 'Без описания'; date = '2026-10-15'; location = 'Москва' } 201
    $createdIds.Add([int]$optional.id)
    if ($optional.description -cne '') { throw 'Optional description must be empty' }
    Request DELETE "/events/$($optional.id)" $null 204
    $createdIds.Remove([int]$optional.id) | Out-Null
    Assert-Fields (DatabaseSnapshot $id) $body
    Compose down
    Compose up -d
    Wait-Api
    Compose ps
    $persisted = Request GET "/events/$id" $null 200
    if ($persisted.id -ne $id) { throw 'Persistence id mismatch' }
    Assert-Fields $persisted $body
    Write-Host "PASS: event $id and all fields persisted after down/up."
    Assert-Fields (DatabaseSnapshot $id) $body
    Request DELETE "/events/$id" $null 204
    $createdIds.Remove($id) | Out-Null
    Request GET "/events/$id" $null 404
    Request PUT "/events/$id" $body 404
    Request DELETE "/events/$id" $null 404
    Request GET /events/not-an-integer $null 404
    Compose logs --no-color --tail 100

    Write-Host 'PASS: CRUD, validation, missing IDs, SQL parameters and persistence verified.'
} catch {
    Write-Host "FAIL: $($_.Exception.Message)"
    try { Compose ps; Compose logs --no-color --tail 100 } catch { Write-Host $_.Exception.Message }
    throw
} finally {
    foreach ($cleanupId in $createdIds) {
        try { Request DELETE "/events/$cleanupId" $null 204 } catch { Write-Host "Cleanup failed for $cleanupId" }
    }
    $client.Dispose()
    Stop-Transcript
}
