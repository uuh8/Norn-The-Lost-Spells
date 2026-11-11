#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ClientExe,
    [string]$UnityRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')),
    [ValidateRange(60,1200)][int]$TimeoutSeconds = 600,
    [ValidateRange(1,120)][int]$MaximumWalkPulses = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'NativeHubInputGuard.ps1')

# 只有主Agent审查后显式运行本文件才启动一个新的Native窗口。正式业务入口沿现有5080配置，
# 所有认证和资产操作都由原主菜单/UGUI/输入完成；不读取Token、不发HTTP、不操作既有用户。
# 初始焦点须由OS正常给本次新窗口；本工具绝不抢焦点。失焦后停止且保留现场，不自动重试输入。
$UnityRoot = [IO.Path]::GetFullPath($UnityRoot)
$ClientExe = [IO.Path]::GetFullPath($ClientExe)
if (!(Test-Path -LiteralPath $ClientExe -PathType Leaf)) { throw 'NativeClientMissing' }
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6)
$privateDirectory = Join-Path $UnityRoot ('.tools/native-hub-regression/' + $stamp)
$output = Join-Path $UnityRoot ('docs/superpowers/evidence/multiplayer/single-player-regression-20261103/hub-native-' + $stamp)
if ((Test-Path -LiteralPath $privateDirectory) -or (Test-Path -LiteralPath $output)) { throw 'HubOutputAlreadyExists' }
New-Item -ItemType Directory -Path $privateDirectory | Out-Null
New-Item -ItemType Directory -Path $output | Out-Null
$configPath = Join-Path $privateDirectory 'probe-config.json'
$hubCommand = Join-Path $privateDirectory 'hub-command.json'
$hubOutput = Join-Path $privateDirectory 'hub-report.json'
$portalCommand = Join-Path $privateDirectory 'portal-command'
$portalOutput = Join-Path $privateDirectory 'portal-report.json'
$config = @{ Output=$portalOutput; Command=$portalCommand; ObserveMovementOnly=$true; MovementSamples=0; HubOutput=$hubOutput; HubCommand=$hubCommand }
[IO.File]::WriteAllText($configPath,($config|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
$owned = $null; $guard = $null; $artifact = 0; $stage = 'Launch'; $passed = $false; $checks = [Collections.Generic.List[object]]::new()
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$summary = [ordered]@{
    Passed=$false; Stage=$stage; Failure=''; RecordedAtUtc=''; OwnedClientId=0; OwnedClientStartTicks=0; ClientExe=$ClientExe
    PhysicalPointerInput=$true; CallbackInvocation=$false; Teleport=$false; AssetInjection=$false; ExistingAccountsUsed=$false
    WalkNavigation='Bounded WASD pulses through original movement; blocked paths fail without teleport or transform writes.'
    Checks=@(); Scope='Original Native main menu and Hub UI on loopback 5080, newly registered temporary account only; no subjective feel or universal navigation claim.'
}
function Save-HubJson([string]$Path,$Value) { [IO.File]::WriteAllText($Path,(ConvertTo-Json -InputObject $Value -Depth 20),[Text.UTF8Encoding]::new($false)) }
function Require-Hub([bool]$Value,[string]$Code) { if(!$Value){throw $Code} }
function Get-HubSnapshot([string]$Label) {
    $guard.AssertValid()
    if([DateTime]::UtcNow -gt $deadline){throw 'HubOverallDeadlineExceeded'}
    $requestId=[Guid]::NewGuid().ToString('N')
    $temporary=$hubCommand+'.tmp'
    Save-HubJson $temporary @{Id=$requestId;Stage=$Label}
    [IO.File]::Move($temporary,$hubCommand,$true)
    $until=[DateTime]::UtcNow.AddSeconds(4)
    do {
        $guard.AssertValid()
        if(Test-Path -LiteralPath $hubOutput){
            try {$value=Get-Content -LiteralPath $hubOutput -Raw|ConvertFrom-Json}catch{$value=$null}
            if($null -ne $value -and $value.Id -ceq $requestId){
                Require-Hub ([string]::IsNullOrEmpty($value.DiagnosticError)) 'HubObserverReportedError'
                Require-Hub ([bool]$value.Focused) 'NativeClientNotFocused'
                return $value
            }
        }
        $guard.WaitGuarded(40)
    }while([DateTime]::UtcNow -lt $until)
    throw ('HubProbeTimeout:'+ $Label)
}
function Wait-Hub([string]$Label,[scriptblock]$Predicate,[int]$Seconds=20){
    $until=[DateTime]::UtcNow.AddSeconds($Seconds)
    do {$value=Get-HubSnapshot $Label;if(& $Predicate $value){return $value};$guard.WaitGuarded(100)}while([DateTime]::UtcNow -lt $until)
    throw ('HubConditionTimeout:'+ $Label)
}
function No-HubPending($Value){return @($Value.Workspaces|Where-Object {$_.AccountPending -or $_.AccountUnknown}).Count -eq 0}
function Test-HubWalletText($Value){
    $visible=@($Value.Wallets|Where-Object {$_.Visible})
    if($visible.Count -eq 0){return $false}
    # 至少一个原钱包可见，且所有可见钱包都必须显示当前Scope余额；合法0不能被truthiness误拒绝。
    foreach($wallet in $visible){
        [long]$displayed=0
        $style=[Globalization.NumberStyles]::Integer -bor [Globalization.NumberStyles]::AllowThousands
        if(![long]::TryParse([string]$wallet.Text,$style,[Globalization.CultureInfo]::InvariantCulture,[ref]$displayed) -or $displayed -ne $Value.Gold){return $false}
    }
    return $true
}
function Find-HubItem($Value,[string]$ItemId){return @($Value.Items|Where-Object {$_.Id -ceq $ItemId})|Select-Object -First 1}
function Get-HubNode($Value,[string]$Kind,[string]$Id=''){
    $found=@($Value.Nodes|Where-Object {$_.Kind -ceq $Kind -and $_.Interactable -and $_.RaycastAccepted -and ($Id.Length -eq 0 -or $_.Id -ceq $Id)})
    Require-Hub ($found.Count -eq 1) ('ExpectedOneRaycastNode:'+ $Kind)
    return $found[0]
}
function Assert-HubHover($Value,$Node){
    Require-Hub ([Math]::Abs($Value.MousePosition.x-$Node.Point.x) -le 2 -and [Math]::Abs($Value.MousePosition.y-$Node.Point.y) -le 2) 'OsPointerDidNotReachUnityPoint'
    Require-Hub (@($Value.RaycastTargets).Count -gt 0) 'PointerHasNoRaycastTarget'
    $hit=[string]$Value.RaycastTargets[0]
    Require-Hub ($hit -ceq $Node.Path -or $hit.StartsWith($Node.Path+'/',[StringComparison]::Ordinal)) 'PointerRaycastMissedOriginalControl'
}
function Get-HubOriginalUiReport {
    # 原探针1Hz按需报告；只接受本次请求之后写完且可解析的新文件，不能沿用上次窗口的槽位几何。
    $guard.AssertValid()
    $requested=[DateTime]::UtcNow
    [IO.File]::WriteAllText($portalCommand+'.ui','capture',[Text.UTF8Encoding]::new($false))
    $uiPath=$portalOutput+'.ui.json';$until=[DateTime]::UtcNow.AddSeconds(3)
    do{
        $guard.AssertValid()
        if((Test-Path -LiteralPath $uiPath) -and (Get-Item -LiteralPath $uiPath).LastWriteTimeUtc -ge $requested){
            try{$raw=Get-Content -LiteralPath $uiPath -Raw;$parsed=$raw|ConvertFrom-Json;return [PSCustomObject]@{Raw=$raw;Value=$parsed}}catch{}
        }
        $guard.WaitGuarded(80)
    }while([DateTime]::UtcNow -lt $until)
    throw 'OriginalUiRaycastEvidenceMissing'
}
function Save-HubStage([string]$Label,$Value){
    $script:artifact++;$prefix=('{0:D2}-{1}' -f $script:artifact,$Label)
    Save-HubJson (Join-Path $output ($prefix+'.json')) $Value
    Save-HubScreenshot $guard (Join-Path $output ($prefix+'.png'))
    # 原UI Probe保留真实InputSystem point、pointer状态和Raycast，不用新Probe结论替代原始输入证据。
    $original=Get-HubOriginalUiReport
    [IO.File]::WriteAllText((Join-Path $output ($prefix+'.ui.json')),$original.Raw,[Text.UTF8Encoding]::new($false))
}
function Assert-HubOriginalUiHover($Value,$Node){
    Require-Hub ($Value.Focused -and $Value.EventSystemFocused -and $Value.ModuleEnabled -and $Value.PointEnabled -and $Value.ClickEnabled) 'OriginalUiInputNotReady'
    Require-Hub ([Math]::Abs($Value.MousePosition.x-$Node.Center.x) -le 2 -and [Math]::Abs($Value.MousePosition.y-$Node.Center.y) -le 2 -and
        [Math]::Abs($Value.UiPoint.x-$Node.Center.x) -le 2 -and [Math]::Abs($Value.UiPoint.y-$Node.Center.y) -le 2) 'OriginalUiPointerDidNotReachWandSlot'
    Require-Hub (@($Value.RaycastTargets).Count -gt 0) 'OriginalUiWandSlotHasNoRaycast'
    $hit=[string]$Value.RaycastTargets[0];$suffix=$hit.IndexOf(' | ',[StringComparison]::Ordinal)
    if($suffix -ge 0){$hit=$hit.Substring(0,$suffix)} # 原报告给Button路径追加active/interactable只读说明。
    Require-Hub ($hit -ceq $Node.Path -or $hit.StartsWith($Node.Path+'/',[StringComparison]::Ordinal)) 'OriginalUiFirstRaycastMissedWandSlot'
    $current=@($Value.Nodes|Where-Object {$_.Kind -ceq 'DropTarget' -and $_.DropKind -ceq 'EquippedWand' -and $_.SlotIndex -eq 1 -and
        $_.OwnerId -ceq $Node.OwnerId -and $_.Path -ceq $Node.Path -and $_.Interactable})
    Require-Hub ($current.Count -eq 1) 'OriginalUiWandSlotChangedDuringDrag'
}
function Click-HubControl([string]$Kind,[string]$Id=''){
    $value=Get-HubSnapshot ('locate-'+$Kind);$node=Get-HubNode $value $Kind $Id
    Move-HubPointer $guard $node.Point.x $node.Point.y $value.Width $value.Height
    $value=Get-HubSnapshot ('hover-'+$Kind);Assert-HubHover $value $node
    Save-HubStage ('hover-'+$Kind) $value
    Click-HubPointer $guard
}
function Walk-HubTo([string]$Kind,[string]$ScenePart=''){
    $stalled=0
    for($pulse=0;$pulse -lt $MaximumWalkPulses;$pulse++){
        $value=Get-HubSnapshot ('walk-'+$Kind)
        Require-Hub ($value.HubReady -and $value.TimeScale -gt 0 -and (No-HubPending $value)) 'HubNavigationBlockedByCurrentState'
        $targets=@($value.Navigation|Where-Object {$_.Kind -ceq $Kind -and ($ScenePart.Length -eq 0 -or $_.ScenePath.Contains($ScenePart))})
        Require-Hub ($targets.Count -gt 0) ('NavigationTargetMissing:'+ $Kind)
        $target=$targets|Sort-Object @{Expression={[Math]::Pow($_.Position.x-$value.ActorPosition.x,2)+[Math]::Pow($_.Position.z-$value.ActorPosition.z,2)}}|Select-Object -First 1
        if(($Kind -ne 'Portal' -and $value.Station -ceq $Kind) -or ($Kind -ceq 'Portal' -and $value.NearestPortalId -eq $target.Id)){Save-HubStage ('arrived-'+$Kind) $value;return}
        $dx=$target.Position.x-$value.ActorPosition.x;$dz=$target.Position.z-$value.ActorPosition.z
        $front=$dx*$value.CameraForward.x+$dz*$value.CameraForward.z;$right=$dx*$value.CameraRight.x+$dz*$value.CameraRight.z
        Require-Hub ([Math]::Abs($front)+[Math]::Abs($right) -gt .001) 'NavigationCannotResolveCameraBasis'
        $key=if([Math]::Abs($front) -ge [Math]::Abs($right)){if($front -ge 0){'W'}else{'S'}}else{if($right -ge 0){'D'}else{'A'}}
        Press-HubKey $guard $key 160
        $after=Get-HubSnapshot ('walk-result-'+$Kind)
        $distance=[Math]::Sqrt([Math]::Pow($after.ActorPosition.x-$value.ActorPosition.x,2)+[Math]::Pow($after.ActorPosition.z-$value.ActorPosition.z,2))
        if($distance -lt .015){$stalled++}else{$stalled=0}
        if($stalled -ge 4){Save-HubStage ('walk-blocked-'+$Kind) $after;throw 'PhysicalWalkBlockedNoTeleportFallback'}
    }
    throw ('PhysicalWalkPulseBudgetExhausted:'+ $Kind)
}
function Drag-HubItem([string]$ItemId,[string]$Destination,[bool]$RequireDifferentCell=$false){
    $before=Get-HubSnapshot ('before-drag-'+$Destination);Require-Hub (No-HubPending $before) 'PreviousHubCommandStillPending'
    $item=Find-HubItem $before $ItemId;Require-Hub ($null -ne $item) 'DragItemNotInTrustedSource'
    $wandSlot=$null
    if($Destination -ceq 'EquippedWand'){
        Require-Hub ($item.Kind -ceq 'Wand' -and $item.LocationKind -ceq 'Backpack') 'WandSlotCoverageRequiresABackpackWand'
        Require-Hub (@($before.Items|Where-Object {$_.LocationKind -ceq 'EquippedWand' -and $_.Slot -eq 1}).Count -eq 0) 'ExpectedEmptyEquippedWandSlotOne'
        $original=Get-HubOriginalUiReport
        $targets=@($original.Value.Nodes|Where-Object {$_.Kind -ceq 'DropTarget' -and $_.DropKind -ceq 'EquippedWand' -and $_.SlotIndex -eq 1 -and
            $_.OwnerId -ceq $before.PlayerId -and $_.Interactable})
        Require-Hub ($targets.Count -eq 1) 'ExpectedOneVisibleOriginalWandSlotOne'
        $wandSlot=$targets[0]
    }
    $node=Get-HubNode $before 'Item' $ItemId
    Move-HubPointer $guard $node.Point.x $node.Point.y $before.Width $before.Height
    $hover=Get-HubSnapshot 'source-raycast';Assert-HubHover $hover $node;Save-HubStage 'drag-source-raycast' $hover
    try{
        Start-HubDrag $guard
        Move-HubDrag $guard ([Math]::Min($before.Width-1,$node.Point.x+14)) $node.Point.y $before.Width $before.Height
        $drag=Wait-Hub 'actual-begin-drag' {param($r) $r.Dragging -and $r.DragItem -ceq $ItemId} 4
        if($Destination -ceq 'Warehouse'){
            $drop=Get-HubNode $drag 'WarehouseDrop'
            Move-HubDrag $guard $drop.Point.x $drop.Point.y $drag.Width $drag.Height
            $dropState=Get-HubSnapshot 'warehouse-drop-raycast';Assert-HubHover $dropState $drop;Save-HubStage 'warehouse-before-release' $dropState
        }elseif($Destination -ceq 'EquippedWand'){
            # 槽位来自新鲜原UI报告；仍用OS Left拖动，不能调用OnDrop或拿背包换格冒充装备。
            $original=Get-HubOriginalUiReport
            Require-Hub ($original.Value.Width -eq $drag.Width -and $original.Value.Height -eq $drag.Height) 'OriginalUiWandSlotViewportMismatch'
            Move-HubDrag $guard $wandSlot.Center.x $wandSlot.Center.y $original.Value.Width $original.Value.Height
            $hoverOriginal=Get-HubOriginalUiReport
            Assert-HubOriginalUiHover $hoverOriginal.Value $wandSlot
            $dropState=Get-HubSnapshot 'equipped-wand-slot-one-raycast'
            Save-HubStage 'equipped-wand-slot-one-before-release' $dropState
        }else{
            # 使用原Preview的合法候选+当前Ghost抓取偏移；跨网格缩放后必须重新读，再校正到实际候选格。
            $landing=$null;$aligned=$false
            for($correction=0;$correction -lt 4;$correction++){
                $candidates=@($drag.Landings|Where-Object {$_.Kind -ceq $Destination -and $_.ItemId -ceq $ItemId -and $_.RaycastAccepted -and
                    (!$RequireDifferentCell -or $_.X -ne $item.X -or $_.Y -ne $item.Y)})
                Require-Hub ($candidates.Count -gt 0) ('NoVisibleLegalOriginalGridLanding:'+ $Destination)
                if($null -eq $landing){$landing=$candidates[0]}else{$landing=@($candidates|Where-Object {$_.GridPath -ceq $landing.GridPath -and $_.X -eq $landing.X -and $_.Y -eq $landing.Y})|Select-Object -First 1}
                Require-Hub ($null -ne $landing) 'GridLandingChangedDuringDrag'
                Move-HubDrag $guard $landing.Point.x $landing.Point.y $drag.Width $drag.Height
                $drag=Get-HubSnapshot 'grid-drag-geometry'
                $grid=@($drag.Grids|Where-Object {$_.Path -ceq $landing.GridPath})|Select-Object -First 1
                if($null -ne $grid -and $grid.DragPlacementKnown -and $grid.DragFits -and $grid.DragX -eq $landing.X -and $grid.DragY -eq $landing.Y -and $grid.Coverage -ge .9){$aligned=$true;break}
            }
            Require-Hub $aligned 'GhostDidNotReachOriginalLegalGridPlacement'
            Save-HubStage ('grid-before-release-'+$Destination) $drag
        }
        End-HubDrag $guard
    }finally{[void]$guard.ReleaseOwnedInput()}
    $expected=if($Destination -ceq 'Nearby'){'WorldItem'}else{$Destination}
    $committed=Wait-Hub ('committed-'+$Destination) {param($r)
        $current=Find-HubItem $r $ItemId
        $r.HubVersion -gt $before.HubVersion -and (No-HubPending $r) -and $r.LastAccountState -ceq 'Committed' -and
            $r.LastAccountCommittedVersion -le $r.HubVersion -and $null -ne $current -and $current.LocationKind -ceq $expected -and
            ($Destination -cne 'EquippedWand' -or $current.Slot -eq 1 -and $current.OwnerId -ceq $before.PlayerId) -and
            ($item.LocationKind -cne 'EquippedWand' -or $Destination -cne 'Backpack' -or @($r.Items|Where-Object {$_.LocationKind -ceq 'EquippedWand' -and $_.Slot -eq 1}).Count -eq 0) -and
            (!$RequireDifferentCell -or $current.X -ne $item.X -or $current.Y -ne $item.Y)
    } 20
    Save-HubStage ('committed-'+$Destination) $committed
    $checks.Add(@{Stage=('PhysicalDrag-'+$Destination);Passed=$true;BeforeVersion=$before.HubVersion;AfterVersion=$committed.HubVersion;PendingCleared=$true;SameItem=$true;
        SourceLocation=$item.LocationKind;TargetLocation=$expected;TargetSlot=$(if($Destination -ceq 'EquippedWand'){1}else{-1});OriginalUiWandSlotRaycast=$($Destination -ceq 'EquippedWand')})
    return $committed
}

try{
    $start=[Diagnostics.ProcessStartInfo]::new();$start.FileName=$ClientExe;$start.WorkingDirectory=Split-Path -Parent $ClientExe
    $start.UseShellExecute=$false;$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Normal
    foreach($argument in @('--norn-portal-acceptance',$configPath,'--norn-hub-flow-acceptance',$configPath,'-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-logFile',(Join-Path $privateDirectory 'native-client.private.log'))){$start.ArgumentList.Add($argument)}
    $owned=[Diagnostics.Process]::Start($start);Require-Hub ($null -ne $owned) 'OwnedNativeLaunchFailed'
    $summary.OwnedClientId=$owned.Id;$summary.OwnedClientStartTicks=$owned.StartTime.ToUniversalTime().Ticks
    $until=[DateTime]::UtcNow.AddSeconds(30)
    do{$owned.Refresh();if($owned.HasExited){throw 'OwnedNativeExitedDuringStartup'};if($owned.MainWindowHandle -ne [IntPtr]::Zero){break};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $until)
    $guard=New-HubInputGuard -Process $owned -ExpectedExe $ClientExe -ExpectedStartTicks $summary.OwnedClientStartTicks
    $stage='MainMenuRegisterLogin'
    $menu=Wait-Hub 'main-menu' {param($r) @($r.Nodes|Where-Object {$_.Kind -ceq 'Register' -and $_.Interactable}).Count -eq 1}
    $username='hubreg_'+[DateTime]::UtcNow.ToString('HHmmss')+'_'+[Guid]::NewGuid().ToString('N').Substring(0,6)
    $password=[Guid]::NewGuid().ToString('N')+'N!7'
    Click-HubControl 'Username';Type-HubText $guard $username
    Click-HubControl 'Password';Type-HubText $guard $password
    Click-HubControl 'Register'
    $null=Wait-Hub 'register-complete' {param($r) !$r.Busy -and !$r.Authenticated -and ([string]$r.Status).Contains('注册成功')}
    Click-HubControl 'Login';$password=$null
    $null=Wait-Hub 'login-complete' {param($r) $r.Authenticated -and !$r.Busy}
    Click-HubControl 'EnterHub'
    $initial=Wait-Hub 'initial-hub' {param($r) $r.HubReady -and !$r.Busy -and (No-HubPending $r) -and
        @($r.Items|Where-Object {$_.Kind -ceq 'Backpack' -and $_.LocationKind -ceq 'EquippedBackpack'}).Count -eq 1} 40
    Require-Hub (@($initial.Items|Where-Object {$_.Kind -ceq 'Backpack' -and $_.LocationKind -ceq 'EquippedBackpack'}).Count -eq 1) 'MinimumBackpackNotInstalled'
    Save-HubStage 'trusted-initial-hub' $initial;$checks.Add(@{Stage='RealMenuRegistrationLoginAndHub';Passed=$true;MinimumBackpack=$true})

    $stage='ShopBuyAndReopen';Walk-HubTo 'Shop';Press-HubKey $guard 'F' 90
    $shop=Wait-Hub 'shop-ready' {param($r) $r.Shop.Open -and $r.Shop.ReadReady -and $r.Shop.CanTrade -and $r.Shop.VisibleRows -gt 0 -and (Test-HubWalletText $r)}
    $visibleOffers=@($shop.Nodes|Where-Object {$_.Kind -ceq 'Offer' -and $_.Interactable -and $_.RaycastAccepted}|ForEach-Object {$_.Id})
    $offer=@($shop.Shop.Offers|Where-Object {$_.Kind -ceq 'Wand' -and $_.Price -gt 0 -and $_.Stock -gt 0 -and $_.Price -le $shop.Gold -and $visibleOffers -ccontains $_.Id}|Sort-Object Price)|Select-Object -First 1
    Require-Hub ($null -ne $offer) 'NoAffordableVisibleWandOffer'
    $oldIds=@($shop.Items|ForEach-Object {$_.Id});Click-HubControl 'Offer' $offer.Id;Click-HubControl 'Buy';Click-HubControl 'ConfirmPurchase'
    $bought=Wait-Hub 'purchase-installed' {param($r) $r.HubVersion -gt $shop.HubVersion -and $r.Gold -eq $shop.Gold-$offer.Price -and (No-HubPending $r) -and
        @($r.Items|Where-Object {$_.ContentId -ceq $offer.ContentId -and $oldIds -cnotcontains $_.Id}).Count -eq 1}
    $purchased=@($bought.Items|Where-Object {$_.ContentId -ceq $offer.ContentId -and $oldIds -cnotcontains $_.Id})[0];$itemId=$purchased.Id
    Save-HubStage 'one-wand-purchase-installed' $bought
    Press-HubKey $guard 'ESC' 90;$null=Wait-Hub 'shop-closed' {param($r) !$r.Shop.Open};Press-HubKey $guard 'F' 90
    $reopened=Wait-Hub 'shop-reopened' {param($r) $r.Shop.Open -and $r.Shop.ReadReady -and $r.Shop.CanTrade -and $r.Shop.VisibleRows -gt 0 -and (Test-HubWalletText $r)}
    Require-Hub ($reopened.HubVersion -eq $bought.HubVersion -and $reopened.Gold -eq $bought.Gold) 'ShopReopenChangedTrustedAssets'
    $newOffer=@($reopened.Shop.Offers|Where-Object {$_.Id -ceq $offer.Id})[0];Require-Hub ($newOffer.Stock -eq $offer.Stock-1) 'ShopReopenStockMismatch'
    Save-HubStage 'shop-reopened-with-real-rows' $reopened;$checks.Add(@{Stage='ShopBuyOneAndReopen';Passed=$true;GoldVerified=$true;VisibleRows=$reopened.Shop.VisibleRows})
    Press-HubKey $guard 'ESC' 90;$null=Wait-Hub 'shop-closed-again' {param($r) !$r.Shop.Open}

    $stage='WarehouseAndTabPhysicalDrags';Walk-HubTo 'Warehouse';Press-HubKey $guard 'F' 90
    $null=Wait-Hub 'original-warehouse-open' {param($r) @($r.Workspaces|Where-Object {$_.Open -and $_.Warehouse}).Count -gt 0 -and $r.CursorVisible -and (Test-HubWalletText $r)}
    $null=Drag-HubItem $itemId 'Warehouse'
    $null=Drag-HubItem $itemId 'Backpack'
    $null=Drag-HubItem $itemId 'EquippedWand'
    $null=Drag-HubItem $itemId 'Backpack'
    Press-HubKey $guard 'ESC' 90;$null=Wait-Hub 'warehouse-closed' {param($r) @($r.Workspaces|Where-Object {$_.Open}).Count -eq 0}
    Press-HubKey $guard 'TAB' 90;$null=Wait-Hub 'original-tab-open' {param($r) @($r.Workspaces|Where-Object {$_.Open -and !$_.Warehouse}).Count -gt 0 -and $r.CursorVisible}
    $null=Drag-HubItem $itemId 'EquippedWand'
    $null=Drag-HubItem $itemId 'Backpack'
    $null=Drag-HubItem $itemId 'Backpack' $true
    $dropped=Drag-HubItem $itemId 'Nearby';$groundOwner=(Find-HubItem $dropped $itemId).OwnerId
    Require-Hub (![string]::IsNullOrEmpty($groundOwner)) 'GroundDropHasNoWorldOwner'
    $picked=Drag-HubItem $itemId 'Backpack';Require-Hub ((Find-HubItem $picked $itemId).OwnerId -cne $groundOwner) 'PickupRetainedGroundOwnership'
    $null=Drag-HubItem $itemId 'Backpack' $true
    Press-HubKey $guard 'ESC' 90;$afterDrags=Wait-Hub 'all-pending-cleared' {param($r) (No-HubPending $r) -and @($r.Workspaces|Where-Object {$_.Open}).Count -eq 0}
    Walk-HubTo 'Shop';Press-HubKey $guard 'F' 90
    $afterShop=Wait-Hub 'shop-after-inventory-updates' {param($r) $r.Shop.Open -and $r.Shop.CanTrade -and $r.Shop.ReadReady -and $r.Shop.VisibleRows -gt 0 -and (Test-HubWalletText $r)}
    Require-Hub ($afterShop.Gold -eq $bought.Gold -and $afterShop.HubVersion -eq $afterDrags.HubVersion) 'ShopAfterDragVersionOrGoldMismatch'
    Save-HubStage 'shop-after-real-drags' $afterShop;$checks.Add(@{Stage='ShopReopenAfterHubProfileAdvances';Passed=$true;VisibleRows=$afterShop.Shop.VisibleRows;HubVersion=$afterShop.HubVersion})
    Press-HubKey $guard 'ESC' 90;$null=Wait-Hub 'shop-final-close' {param($r) !$r.Shop.Open}

    $stage='SinglePlayerEntryAndReliableExit';Walk-HubTo 'Portal' 'P7';Press-HubKey $guard 'F' 90
    $null=Wait-Hub 'single-player-choice' {param($r) @($r.Nodes|Where-Object {$_.Kind -ceq 'SinglePlayer' -and $_.Interactable -and $_.RaycastAccepted}).Count -eq 1}
    Click-HubControl 'SinglePlayer'
    $entered=Wait-Hub 'native-single-player-entered' {param($r) $r.OnlineConnected -and $r.OnlineInventoryReady -and !$r.HubActive} 90
    Save-HubStage 'native-single-player-owner-ready' $entered
    Press-HubKey $guard 'ESC' 90;$null=Wait-Hub 'original-exit-button' {param($r) @($r.Nodes|Where-Object {$_.Kind -ceq 'RoomCancelOrExit' -and $_.Interactable -and $_.RaycastAccepted}).Count -eq 1}
    Click-HubControl 'RoomCancelOrExit'
    $returned=Wait-Hub 'reliable-exit-hub-profile' {param($r) $r.ExitPhase -ceq 'Committed' -and $r.HubReady -and $r.HubActive -and (No-HubPending $r) -and
        $r.HubVersion -ge $r.ExitCommittedVersion -and $null -ne (Find-HubItem $r $itemId)} 60
    Save-HubStage 'saved-return-with-real-hub-profile' $returned
    $checks.Add(@{Stage='NormalSinglePlayerEntryAndSavedReturn';Passed=$true;ExitCommittedVersion=$returned.ExitCommittedVersion;HubVersion=$returned.HubVersion;PurchasedItemPreserved=$true})
    $passed=$true
}catch{
    $message=$_.Exception.Message
    $summary.Failure=if($message -cmatch '^[A-Za-z][A-Za-z0-9:._-]{0,160}$'){$message}else{$_.Exception.GetType().Name}
    if($null -ne $guard){try{$failureSnapshot=Get-HubSnapshot 'failure';Save-HubStage 'failure' $failureSnapshot}catch{}}
}finally{
    $password=$null
    if($null -ne $guard){try{Close-HubInputGuard $guard}catch{$passed=$false;$summary.Failure='OwnedInputCleanupFailed'}}
    $summary.Passed=$passed;$summary.Stage=$stage;$summary.RecordedAtUtc=[DateTime]::UtcNow.ToString('O');$summary.Checks=$checks.ToArray()
    Save-HubJson (Join-Path $output 'evidence.json') $summary
    # 故障保留Native现场，由主Agent核对并走原退出；不批量kill，也不为清理直接修改Run/资产表。
}
if(!$passed){throw ('NativeHubRegressionFailed:'+ $summary.Failure + '; evidence='+$output)}
Write-Output ('NativeHubRegressionPassed; evidence='+$output)
