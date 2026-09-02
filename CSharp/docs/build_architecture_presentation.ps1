param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot "presentation")
)

$ErrorActionPreference = "Stop"

function Color([int]$r, [int]$g, [int]$b) { return $r + ($g * 256) + ($b * 65536) }

$C = @{
    Navy      = Color 24 38 53
    Navy2     = Color 31 49 68
    Ink       = Color 31 41 55
    Slate     = Color 88 104 122
    Muted     = Color 113 128 145
    Line      = Color 205 215 225
    Fog       = Color 243 247 250
    White     = Color 255 255 255
    Blue      = Color 31 111 181
    BlueSoft  = Color 226 239 250
    Teal      = Color 42 157 143
    TealSoft  = Color 225 246 241
    Mint      = Color 104 211 145
    Orange    = Color 244 162 97
    OrangeSoft= Color 253 239 224
    Red       = Color 211 78 78
    RedSoft   = Color 252 232 232
    Purple    = Color 119 91 163
    PurpleSoft= Color 239 233 247
    Green     = Color 64 145 108
    GreenSoft = Color 230 244 236
}

$script:SlideW = 960.0
$script:SlideH = 540.0

function Set-Background($slide, [int]$color) {
    $slide.FollowMasterBackground = 0
    $slide.Background.Fill.ForeColor.RGB = $color
    $slide.Background.Fill.Solid()
}

function Add-Text($slide, [double]$x, [double]$y, [double]$w, [double]$h,
                  [string]$text, [double]$size = 16, [int]$color = 0,
                  [string]$font = "Aptos", [bool]$bold = $false,
                  [int]$align = 1, [int]$valign = 1) {
    try {
        $shape = $slide.Shapes.AddTextbox(1, $x, $y, $w, $h)
    }
    catch {
        throw "AddTextbox failed on slide $($slide.SlideIndex): x=$x y=$y w=$w h=$h text='$text'. $($_.Exception.Message)"
    }
    $shape.TextFrame.MarginLeft = 0
    $shape.TextFrame.MarginRight = 0
    $shape.TextFrame.MarginTop = 0
    $shape.TextFrame.MarginBottom = 0
    $shape.TextFrame.VerticalAnchor = $valign
    $shape.TextFrame.TextRange.Text = $text
    $shape.TextFrame.TextRange.Font.Name = $font
    $shape.TextFrame.TextRange.Font.Size = $size
    $shape.TextFrame.TextRange.Font.Bold = [int]$bold
    $shape.TextFrame.TextRange.Font.Color.RGB = $color
    $shape.TextFrame.TextRange.ParagraphFormat.Alignment = $align
    return $shape
}

function Add-Box($slide, [double]$x, [double]$y, [double]$w, [double]$h,
                 [int]$fill, [int]$line, [double]$radius = 8, [double]$lineWidth = 1) {
    $type = if ($radius -gt 0) { 5 } else { 1 }
    $shape = $slide.Shapes.AddShape($type, $x, $y, $w, $h)
    $shape.Fill.ForeColor.RGB = $fill
    $shape.Fill.Solid()
    $shape.Line.ForeColor.RGB = $line
    $shape.Line.Weight = $lineWidth
    return $shape
}

function Add-Line($slide, [double]$x1, [double]$y1, [double]$x2, [double]$y2,
                  [int]$color, [double]$width = 1.5, [bool]$arrow = $false,
                  [int]$dash = 1) {
    $line = $slide.Shapes.AddLine($x1, $y1, $x2, $y2)
    $line.Line.ForeColor.RGB = $color
    $line.Line.Weight = $width
    $line.Line.DashStyle = $dash
    if ($arrow) { $line.Line.EndArrowheadStyle = 3 }
    return $line
}

function Add-Pill($slide, [double]$x, [double]$y, [double]$w, [string]$text,
                  [int]$fill, [int]$textColor) {
    Add-Box $slide $x $y $w 24 $fill $fill 12 | Out-Null
    Add-Text $slide ($x + 8) ($y + 3) ($w - 16) 18 $text 10 $textColor "Aptos" $true 2 3 | Out-Null
}

function Add-CardText($slide, [double]$x, [double]$y, [double]$w, [double]$h,
                      [string]$title, [string]$body, [int]$accent,
                      [int]$fill = 0, [double]$titleSize = 15, [double]$bodySize = 11.5) {
    if ($fill -eq 0) { $fill = $C.White }
    Add-Box $slide $x $y $w $h $fill $C.Line 8 | Out-Null
    Add-Box $slide $x $y 5 $h $accent $accent 0 | Out-Null
    Add-Text $slide ($x + 18) ($y + 14) ($w - 32) 24 $title $titleSize $C.Ink "Aptos Display" $true | Out-Null
    Add-Text $slide ($x + 18) ($y + 43) ($w - 32) ($h - 55) $body $bodySize $C.Slate "Aptos" $false 1 1 | Out-Null
}

function Add-SlideTitle($slide, [string]$title, [string]$kicker, [int]$number) {
    Add-Text $slide 40 24 650 18 $kicker.ToUpperInvariant() 10 $C.Teal "Aptos" $true | Out-Null
    Add-Text $slide 40 45 830 42 $title 26 $C.Navy "Aptos Display" $true | Out-Null
    Add-Text $slide 892 31 28 22 ($number.ToString("00")) 10 $C.Muted "Aptos" $true 3 | Out-Null
    Add-Line $slide 40 92 920 92 $C.Line 1 | Out-Null
}

function Add-Footer($slide, [string]$text = "Repository Change Workbench  |  Architecture presentation") {
    Add-Line $slide 40 512 920 512 $C.Line 0.8 | Out-Null
    Add-Text $slide 40 518 640 12 $text 8.5 $C.Muted | Out-Null
    Add-Text $slide 780 518 140 12 "Microsoft Agent Framework" 8.5 $C.Muted "Aptos" $false 3 | Out-Null
}

function Add-Node($slide, [double]$x, [double]$y, [double]$w, [double]$h,
                  [string]$title, [string]$sub, [int]$fill, [int]$stroke,
                  [int]$titleColor = 0) {
    if ($titleColor -eq 0) { $titleColor = $C.Ink }
    Add-Box $slide $x $y $w $h $fill $stroke 8 1.2 | Out-Null
    Add-Text $slide ($x + 10) ($y + 9) ($w - 20) 20 $title 13 $titleColor "Aptos" $true 2 3 | Out-Null
    if ($sub) { Add-Text $slide ($x + 10) ($y + 31) ($w - 20) ($h - 37) $sub 9.5 $C.Slate "Aptos" $false 2 1 | Out-Null }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$pptxPath = Join-Path $OutputDirectory "MAF_Repository_Change_Workbench_Architecture.pptx"
$pdfPath = Join-Path $OutputDirectory "MAF_Repository_Change_Workbench_Architecture.pdf"
$uiImage = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\provider-desktop.png"

$powerPoint = $null
$presentation = $null
try {
    $powerPoint = New-Object -ComObject PowerPoint.Application
    $powerPoint.Visible = -1
    $presentation = $powerPoint.Presentations.Add()
    $presentation.PageSetup.SlideWidth = $script:SlideW
    $presentation.PageSetup.SlideHeight = $script:SlideH

    # 01 - Cover
    $s = $presentation.Slides.Add(1, 12)
    Set-Background $s $C.Navy
    Add-Box $s 0 0 960 7 $C.Mint $C.Mint 0 | Out-Null
    Add-Pill $s 54 56 180 "ARCHITECTURE DEEP DIVE" $C.Teal $C.White
    Add-Text $s 54 112 760 112 "Repository Change`nWorkbench" 44 $C.White "Aptos Display" $true | Out-Null
    Add-Text $s 57 238 700 55 "A human-gated, multi-agent system for safe repository change" 20 $C.Mint "Aptos" $false | Out-Null
    Add-Line $s 57 318 900 318 $C.Slate 1 | Out-Null
    Add-Text $s 57 344 620 60 "Big picture -> layers -> relationships -> trust boundaries -> deployment" 14 $C.White | Out-Null
    Add-Text $s 57 468 500 20 "C# / .NET 10  |  Microsoft Agent Framework  |  GitHub" 11 $C.Line | Out-Null
    Add-Box $s 755 356 145 92 $C.Navy2 $C.Teal 8 1.5 | Out-Null
    Add-Text $s 772 373 110 19 "CORE IDEA" 9.5 $C.Mint "Aptos" $true 2 | Out-Null
    Add-Text $s 770 397 115 38 "AI proposes.`nHumans publish." 13 $C.White "Aptos" $true 2 | Out-Null

    # 02 - Executive big picture
    $s = $presentation.Slides.Add(2, 12); Set-Background $s $C.White
    Add-SlideTitle $s "The system turns intent into an inspectable patch - not an automatic deployment" "Executive view" 2
    $y = 188; $w = 128; $h = 104
    Add-Node $s 42 $y $w $h "1  Intent" "Repo + branch`nprovider + task" $C.BlueSoft $C.Blue
    Add-Node $s 200 $y $w $h "2  Session" "Validate inputs`nclone workspace" $C.Fog $C.Slate
    Add-Node $s 358 $y $w $h "3  Agent team" "Plan -> edit ->`nreview -> secure" $C.PurpleSoft $C.Purple
    Add-Node $s 516 $y $w $h "4  Patch" "Exact Git diff`nheld for review" $C.OrangeSoft $C.Orange
    Add-Node $s 674 $y $w $h "5  Human gate" "Reject or approve`ncommit / push" $C.TealSoft $C.Teal
    Add-Node $s 832 $y 88 $h "6  GitHub" "Branch`n+ PR" $C.GreenSoft $C.Green
    foreach($x in @(170,328,486,644,802)){ Add-Line $s $x 240 ($x+28) 240 $C.Teal 2 $true | Out-Null }
    Add-Pill $s 42 119 177 "UNTRUSTED INPUT" $C.RedSoft $C.Red
    Add-Pill $s 366 119 178 "CONSTRAINED AI" $C.PurpleSoft $C.Purple
    Add-Pill $s 682 119 195 "EXPLICIT AUTHORITY" $C.TealSoft $C.Green
    Add-CardText $s 42 335 274 135 "Safety invariant" "Models can inspect and propose changes. They cannot run repository code, invoke a shell, deploy, or push to the base branch." $C.Red $C.RedSoft 15 12
    Add-CardText $s 343 335 274 135 "Quality invariant" "Correctness and security are graph-enforced gates. Only the release manager can emit the completion marker." $C.Purple $C.PurpleSoft 15 12
    Add-CardText $s 644 335 276 135 "Authority invariant" "A patch remains uncommitted until the user approves; push and pull-request creation are separate explicit actions." $C.Teal $C.TealSoft 15 12
    Add-Footer $s

    # 03 - Context and boundaries
    $s = $presentation.Slides.Add(3, 12); Set-Background $s $C.White
    Add-SlideTitle $s "System context: one local control plane mediates three external boundaries" "Context & trust" 3
    Add-Box $s 250 135 460 300 $C.Fog $C.Navy 12 2 | Out-Null
    Add-Text $s 275 151 410 27 "LOCAL REPOSITORY CHANGE WORKBENCH" 13 $C.Navy "Aptos" $true 2 | Out-Null
    Add-Node $s 305 206 140 82 "Browser UI" "input | activity`ndiff | approval" $C.White $C.Blue
    Add-Node $s 515 206 140 82 "ASP.NET API" "sessions | state`nactions" $C.White $C.Blue
    Add-Line $s 445 247 515 247 $C.Blue 2 $true | Out-Null
    Add-Node $s 305 325 140 82 "Agent runtime" "handoff graph`nprovider client" $C.White $C.Purple
    Add-Node $s 515 325 140 82 "Isolated clone" "safe tools`nGit diff" $C.White $C.Orange
    Add-Line $s 445 366 515 366 $C.Purple 2 $true | Out-Null
    Add-Line $s 585 288 585 325 $C.Slate 1.5 $true | Out-Null
    Add-Line $s 375 288 375 325 $C.Slate 1.5 $true | Out-Null
    Add-Node $s 35 220 155 96 "Human operator" "Supplies credentials,`nreviews exact diff,`ngrants publication" $C.TealSoft $C.Teal
    Add-Line $s 190 268 250 268 $C.Teal 2 $true | Out-Null
    Add-Node $s 770 135 155 96 "Model provider" "OpenAI / DeepSeek /`nAnthropic over HTTPS" $C.PurpleSoft $C.Purple
    Add-Line $s 710 183 770 183 $C.Purple 2 $true | Out-Null
    Add-Node $s 770 339 155 96 "GitHub" "Clone source, optional`nbranch push and PR" $C.GreenSoft $C.Green
    Add-Line $s 710 387 770 387 $C.Green 2 $true | Out-Null
    Add-Pill $s 34 461 255 "Credentials live only in server memory" $C.Navy $C.White
    Add-Pill $s 352 461 238 "Repo + model output are untrusted" $C.RedSoft $C.Red
    Add-Pill $s 652 461 273 "No authentication: keep service local" $C.OrangeSoft $C.Orange
    Add-Footer $s

    # 04 - Layer map
    $s = $presentation.Slides.Add(4, 12); Set-Background $s $C.White
    Add-SlideTitle $s "Seven layers separate experience, control, reasoning, data, and publication" "Layer map" 4
    $layers = @(
        @{Y=115; N="01  EXPERIENCE"; T="Browser workbench"; D="Connect | monitor agent activity | inspect diff | approve/reject"; F=$C.BlueSoft; A=$C.Blue},
        @{Y=165; N="02  HTTP CONTROL"; T="ASP.NET Core API"; D="Validation | endpoints | static UI | error mapping"; F=$C.Fog; A=$C.Slate},
        @{Y=215; N="03  SESSION"; T="RepositorySessionService"; D="In-memory state | cancellation | 20-minute timeout | action gating"; F=$C.TealSoft; A=$C.Teal},
        @{Y=265; N="04  ORCHESTRATION"; T="HandoffWorkflowRunner + HandoffWorkflowFactory"; D="Event stream | constrained routing | termination | loop ceilings"; F=$C.PurpleSoft; A=$C.Purple},
        @{Y=315; N="05  AGENTS & MODELS"; T="Six agents + provider adapter"; D="Role prompts | scoped tools | OpenAI / DeepSeek / Anthropic"; F=$C.BlueSoft; A=$C.Blue},
        @{Y=365; N="06  REPOSITORY BOUNDARY"; T="RepositoryWorkspace + safe tools"; D="Isolated clone | bounded text operations | diff | Git process"; F=$C.OrangeSoft; A=$C.Orange},
        @{Y=415; N="07  EXTERNAL SYSTEMS"; T="Model endpoint + GitHub"; D="Inference | clone | optional push | pull request API"; F=$C.GreenSoft; A=$C.Green}
    )
    foreach($l in $layers){
        Add-Box $s 56 $l.Y 848 40 $l.F $l.A 5 1 | Out-Null
        Add-Text $s 70 ($l.Y+9) 142 20 $l.N 10 $l.A "Aptos" $true | Out-Null
        Add-Text $s 220 ($l.Y+8) 285 21 $l.T 13 $C.Ink "Aptos" $true | Out-Null
        Add-Text $s 520 ($l.Y+9) 366 20 $l.D 10.5 $C.Slate | Out-Null
    }
    Add-Text $s 67 474 820 24 "Dependency direction: upper layers call downward; state and events flow back upward." 12 $C.Navy "Aptos" $true 2 | Out-Null
    Add-Footer $s

    # 05 - Experience and API
    $s = $presentation.Slides.Add(5, 12); Set-Background $s $C.White
    Add-SlideTitle $s "The workbench exposes the complete lifecycle without exposing secrets" "Experience + API" 5
    if (Test-Path -LiteralPath $uiImage) {
        $pic = $s.Shapes.AddPicture($uiImage, 0, -1, 50, 122, 560, 350)
        $pic.Line.ForeColor.RGB = $C.Line; $pic.Line.Weight = 1
    }
    Add-Pill $s 70 137 92 "CONNECT" $C.Blue $C.White
    Add-Pill $s 250 137 108 "AGENT WORK" $C.Purple $C.White
    Add-Pill $s 423 137 102 "REVIEW DIFF" $C.Orange $C.White
    Add-CardText $s 640 122 280 82 "Session creation" "POST /api/sessions validates provider, task, repo and credentials; returns an opaque ID." $C.Blue $C.BlueSoft 14 10.5
    Add-CardText $s 640 217 280 82 "Progress polling" "GET /api/sessions/{id} returns status, normalized events, diff, errors and approval metadata." $C.Purple $C.PurpleSoft 14 10.5
    Add-CardText $s 640 312 280 82 "Consequential actions" "Cancel, reject, approve, push and pull request each have their own gated endpoint." $C.Teal $C.TealSoft 14 10.5
    Add-CardText $s 640 407 280 64 "Secret discipline" "Tokens remain in the in-memory server session and are never returned." $C.Red $C.RedSoft 14 10.5
    Add-Footer $s "Actual desktop workbench UI  |  API relationships"

    # 06 - Request sequence
    $s = $presentation.Slides.Add(6, 12); Set-Background $s $C.White
    Add-SlideTitle $s "Request sequence: state crosses the UI boundary; authority does not" "Runtime sequence" 6
    $xs = @(90,270,460,650,840); $names=@("Browser","Session service","Workspace","Agent workflow","GitHub")
    for($i=0;$i -lt $xs.Count;$i++){
        Add-Text $s ($xs[$i]-55) 112 110 20 $names[$i] 11 $C.Navy "Aptos" $true 2 | Out-Null
        Add-Line $s $xs[$i] 137 $xs[$i] 468 $C.Line 1 $false 4 | Out-Null
    }
    $events=@(
        @{Y=160; A=0; B=1; T="create session (inputs + tokens)"; C=$C.Blue},
        @{Y=202; A=1; B=2; T="validate URL / branch; shallow clone"; C=$C.Orange},
        @{Y=244; A=1; B=3; T="build fresh client + handoff workflow"; C=$C.Purple},
        @{Y=286; A=3; B=2; T="read / write via scoped tools"; C=$C.Purple},
        @{Y=328; A=3; B=1; T="messages, handoffs, tools, decision"; C=$C.Teal},
        @{Y=370; A=1; B=0; T="poll snapshot + render exact diff"; C=$C.Blue},
        @{Y=412; A=0; B=1; T="approve(commit, push?) / reject"; C=$C.Teal},
        @{Y=454; A=1; B=4; T="optional branch push / create PR"; C=$C.Green}
    )
    foreach($e in $events){
        $x1=$xs[$e.A];$x2=$xs[$e.B]
        Add-Line $s $x1 $e.Y $x2 $e.Y $e.C 1.8 $true | Out-Null
        Add-Text $s ([Math]::Min($x1,$x2)+8) ($e.Y-17) ([Math]::Abs($x2-$x1)-16) 15 $e.T 9.5 $e.C "Aptos" $true 2 | Out-Null
    }
    Add-Footer $s

    # 07 - Handoff topology
    $s = $presentation.Slides.Add(7, 12); Set-Background $s $C.White
    Add-SlideTitle $s "The handoff graph encodes separation of duties - and only one happy path" "Agent topology" 7
    $nodes=@{
        O=@{X=42;Y=220;T="Intake";S="constraints";F=$C.Fog;A=$C.Slate}
        P=@{X=190;Y=220;T="Planner";S="design";F=$C.BlueSoft;A=$C.Blue}
        D=@{X=338;Y=220;T="Developer";S="edit";F=$C.OrangeSoft;A=$C.Orange}
        R=@{X=486;Y=220;T="Reviewer";S="quality";F=$C.PurpleSoft;A=$C.Purple}
        S=@{X=634;Y=220;T="Security";S="threat gate";F=$C.RedSoft;A=$C.Red}
        M=@{X=782;Y=220;T="Release mgr";S="evidence";F=$C.TealSoft;A=$C.Teal}
    }
    foreach($n in $nodes.Values){Add-Node $s $n.X $n.Y 118 76 $n.T $n.S $n.F $n.A}
    foreach($pair in @(@('O','P'),@('P','D'),@('D','R'),@('R','S'),@('S','M'))){$a=$nodes[$pair[0]];$b=$nodes[$pair[1]];Add-Line $s ($a.X+118) 258 $b.X 258 $C.Teal 2.3 $true | Out-Null}
    Add-Pill $s 157 142 648 "NORMAL PATH  |  accept -> plan -> implement -> review -> secure -> release" $C.Navy $C.White
    Add-Line $s 545 220 545 171 $C.Purple 1.6 | Out-Null; Add-Line $s 545 171 397 171 $C.Purple 1.6 | Out-Null; Add-Line $s 397 171 397 220 $C.Purple 1.6 $true | Out-Null
    Add-Text $s 431 176 82 15 "fix quality" 9 $C.Purple "Aptos" $true 2 | Out-Null
    Add-Line $s 693 296 693 335 $C.Red 1.6 | Out-Null; Add-Line $s 693 335 397 335 $C.Red 1.6 | Out-Null; Add-Line $s 397 335 397 296 $C.Red 1.6 $true | Out-Null
    Add-Text $s 500 338 92 15 "remediate risk" 9 $C.Red "Aptos" $true 2 | Out-Null
    Add-Line $s 841 220 841 117 $C.Teal 1.6 | Out-Null; Add-Line $s 841 117 249 117 $C.Teal 1.6 | Out-Null; Add-Line $s 249 117 249 220 $C.Teal 1.6 $true | Out-Null
    Add-Text $s 509 101 118 15 "design gap -> planner" 9 $C.Teal "Aptos" $true 2 | Out-Null
    Add-Line $s 841 296 841 390 $C.Teal 1.6 | Out-Null; Add-Line $s 841 390 397 390 $C.Teal 1.6 | Out-Null; Add-Line $s 397 390 397 296 $C.Teal 1.6 $true | Out-Null
    Add-Text $s 555 395 143 15 "implementation gap -> developer" 9 $C.Teal "Aptos" $true 2 | Out-Null
    Add-CardText $s 42 410 267 84 "Termination" "Only release_manager + WORKFLOW_COMPLETE = success." $C.Teal $C.TealSoft 13 10
    Add-CardText $s 346 410 267 84 "Loop guard" "12 autonomous turns per agent; 40 total messages." $C.Purple $C.PurpleSoft 13 10
    Add-CardText $s 650 410 270 84 "Developer guard" "Empty diff blocks a handoff to reviewer (2 retries)." $C.Orange $C.OrangeSoft 13 10
    Add-Footer $s

    # 08 - Roles and tool rights
    $s = $presentation.Slides.Add(8, 12); Set-Background $s $C.White
    Add-SlideTitle $s "Agent roles are differentiated by responsibility, routing, and tool authority" "Roles & permissions" 8
    $headers=@("AGENT","ACCOUNTABILITY","TOOLS","MAY HAND OFF TO")
    $widths=@(150,305,190,235); $x=40
    for($i=0;$i -lt 4;$i++){Add-Box $s $x 112 $widths[$i] 34 $C.Navy $C.Navy 0 | Out-Null;Add-Text $s ($x+10) 121 ($widths[$i]-20) 16 $headers[$i] 9.5 $C.White "Aptos" $true | Out-Null;$x+=$widths[$i]}
    $rows=@(
        @("Intake","Capture intent and constraints","None","Planner"),
        @("Planner","Inspect repository; plan + criteria","Read-only","Developer"),
        @("Developer","Implement, test artifacts, remediate","Read + write","Reviewer"),
        @("Reviewer","Independent correctness and quality gate","Read-only","Developer / Security"),
        @("Security","Threat model and security gate","Read-only","Developer / Release mgr"),
        @("Release manager","Verify evidence; authorize completion","Read-only","Planner / Developer / Done")
    )
    $y=146
    for($r=0;$r -lt $rows.Count;$r++){
        $fill=if($r%2 -eq 0){$C.Fog}else{$C.White};$x=40
        for($i=0;$i -lt 4;$i++){
            Add-Box $s $x $y $widths[$i] 53 $fill $C.Line 0 0.6 | Out-Null
            $col=if($i -eq 0){$C.Navy}else{$C.Slate};$bold=($i -eq 0)
            Add-Text $s ($x+10) ($y+9) ($widths[$i]-20) 36 $rows[$r][$i] 10.5 $col "Aptos" $bold 1 1 | Out-Null
            $x+=$widths[$i]
        }
        $y+=53
    }
    Add-Box $s 40 475 420 30 $C.BlueSoft $C.Blue 5 | Out-Null
    Add-Text $s 53 482 394 16 "Read-only = list, read, search, status, diff" 10.5 $C.Blue "Aptos" $true | Out-Null
    Add-Box $s 500 475 420 30 $C.OrangeSoft $C.Orange 5 | Out-Null
    Add-Text $s 513 482 394 16 "Write = complete-file write + exact single-occurrence replace" 10.5 $C.Orange "Aptos" $true | Out-Null
    Add-Footer $s

    # 09 - Provider layer
    $s = $presentation.Slides.Add(9, 12); Set-Background $s $C.White
    Add-SlideTitle $s "One provider definition drives validation, client selection, and reasoning options" "Model abstraction" 9
    Add-Node $s 52 184 178 132 "Provider definition" "id | name | HTTPS URL`ntool calling | token limits`nthinking | effort values" $C.BlueSoft $C.Blue
    Add-Node $s 282 184 178 132 "Normalize + validate" "strip /chat/completions`nreject credentials/query`nrequire tool calling" $C.Fog $C.Slate
    Add-Node $s 512 184 178 132 "Client factory" "Anthropic adapter`nDeepSeek native adapter`nOpenAI chat client" $C.PurpleSoft $C.Purple
    Add-Node $s 742 184 178 132 "Shared IChatClient" "six role-specific agents`nprovider-aware reasoning`nstreaming updates" $C.TealSoft $C.Teal
    Add-Line $s 230 250 282 250 $C.Blue 2 $true | Out-Null
    Add-Line $s 460 250 512 250 $C.Slate 2 $true | Out-Null
    Add-Line $s 690 250 742 250 $C.Purple 2 $true | Out-Null
    Add-Pill $s 59 129 160 "CONFIGURATION INPUT" $C.Blue $C.White
    Add-Pill $s 516 129 168 "RUNTIME ADAPTER" $C.Purple $C.White
    Add-CardText $s 52 360 264 106 "OpenAI-compatible" "OpenAI endpoints use ChatClient. Full chat-completions URLs are normalized to the SDK base endpoint." $C.Blue $C.BlueSoft 14 11
    Add-CardText $s 348 360 264 106 "DeepSeek" "The provider host selects its native MAF adapter; selected reasoning and reasoning_content history are preserved." $C.Purple $C.PurpleSoft 14 11
    Add-CardText $s 644 360 276 106 "Anthropic" "The official Anthropic C# IChatClient integration is selected by host; effort mapping is not forced." $C.Teal $C.TealSoft 14 11
    Add-Footer $s

    # 10 - Repository boundary
    $s = $presentation.Slides.Add(10, 12); Set-Background $s $C.White
    Add-SlideTitle $s "The tool boundary - not the prompt - is the primary security control" "Repository sandbox" 10
    Add-Box $s 320 125 600 342 $C.Fog $C.Orange 12 2 | Out-Null
    Add-Text $s 344 141 550 20 "ISOLATED TEMPORARY CLONE" 12 $C.Orange "Aptos" $true 2 | Out-Null
    Add-Node $s 35 176 218 112 "Model-selected tool call" "Structured function name + arguments`nNo shell command surface" $C.PurpleSoft $C.Purple
    Add-Line $s 253 232 320 232 $C.Purple 2 $true | Out-Null
    Add-CardText $s 348 184 250 102 "Path controls" "Relative paths only | no traversal | no .git | no symbolic links | must stay below clone root" $C.Red $C.White 14 10.5
    Add-CardText $s 634 184 250 102 "Content controls" "Allowlisted source extensions/names | text only | 200 KB/file | exact replacement semantics" $C.Orange $C.White 14 10.5
    Add-CardText $s 348 310 250 102 "Git controls" "Structured ArgumentList | no shell | hooks disabled | LFS smudge disabled | output bounded" $C.Blue $C.White 14 10.5
    Add-CardText $s 634 310 250 102 "Credential controls" "GitHub token via process environment header | never placed in clone URL | terminal prompts disabled" $C.Teal $C.White 14 10.5
    Add-Pill $s 36 340 218 "DELIBERATELY UNAVAILABLE" $C.Red $C.White
    Add-Text $s 45 378 200 71 "- run repository code`n- arbitrary shell`n- install packages`n- deploy infrastructure" 12 $C.Red "Aptos" $true | Out-Null
    Add-Footer $s

    # 11 - Change and approval lifecycle
    $s = $presentation.Slides.Add(11, 12); Set-Background $s $C.White
    Add-SlideTitle $s "Publication is a two-stage gate: approve the patch, then choose its reach" "Human approval" 11
    $stages=@(
        @{X=42;T="CLONED";S="isolated copy";F=$C.Fog;A=$C.Slate},
        @{X=190;T="RUNNING";S="agents edit";F=$C.PurpleSoft;A=$C.Purple},
        @{X=338;T="AWAITING`nAPPROVAL";S="exact diff";F=$C.OrangeSoft;A=$C.Orange},
        @{X=528;T="COMMITTED";S="new local branch";F=$C.BlueSoft;A=$C.Blue},
        @{X=676;T="PUSHED";S="new remote branch";F=$C.TealSoft;A=$C.Teal},
        @{X=824;T="PR CREATED";S="into base branch";F=$C.GreenSoft;A=$C.Green}
    )
    foreach($p in $stages){Add-Node $s $p.X 210 105 82 $p.T $p.S $p.F $p.A}
    foreach($x in @(147,295,443,633,781)){Add-Line $s $x 251 ($x+38) 251 $C.Teal 2 $true | Out-Null}
    Add-Pill $s 343 132 172 "HUMAN DECISION POINT" $C.Orange $C.White
    Add-Line $s 390 292 390 357 $C.Red 2 $true | Out-Null
    Add-Node $s 324 365 132 74 "REJECTED" "no commit`nclone retained locally" $C.RedSoft $C.Red
    Add-Text $s 42 329 241 75 "Before approval`n- base branch untouched`n- no remote write`n- diff remains inspectable" 11.5 $C.Slate | Out-Null
    Add-Text $s 524 329 168 75 "Approval creates`n- new branch`n- one commit`n- deterministic identity" 11.5 $C.Slate | Out-Null
    Add-Text $s 730 329 190 75 "Further reach requires`n- push checkbox/token`n- separate PR action`n- never target base directly" 11.5 $C.Slate | Out-Null
    Add-Box $s 42 435 878 42 $C.TealSoft $C.Teal 5 | Out-Null
    Add-Text $s 58 447 846 18 "Commit, push, and pull request are distinct transitions with separate preconditions and failure recovery." 11.5 $C.Teal "Aptos" $true 2 | Out-Null
    Add-Footer $s

    # 12 - State and event model
    $s = $presentation.Slides.Add(12, 12); Set-Background $s $C.White
    Add-SlideTitle $s "A thread-safe session aggregate converts streaming workflow output into a pollable product state" "State + observability" 12
    Add-Box $s 45 128 340 324 $C.Navy $C.Navy 10 | Out-Null
    Add-Text $s 67 149 296 23 "SESSION SNAPSHOT" 12 $C.Mint "Aptos" $true | Out-Null
    Add-Text $s 67 188 296 220 "id`nrepositoryUrl + baseBranch`ntask`nstatus`nevents[<= 500]`ndiff`nerror`napproval { branch, commit, pushed }`npullRequest { number, url }" 14 $C.White "Consolas" $false | Out-Null
    Add-Text $s 67 419 295 15 "Secrets intentionally absent" 10 $C.Mint "Aptos" $true | Out-Null
    $eventRows=@(
        @{Y=129;T="message";D="streamed agent text";C=$C.Blue},
        @{Y=177;T="handoff";D="source -> target";C=$C.Purple},
        @{Y=225;T="tool";D="observed function call";C=$C.Orange},
        @{Y=273;T="trace";D="lifecycle / PR detail";C=$C.Slate},
        @{Y=321;T="decision";D="completion marker";C=$C.Teal},
        @{Y=369;T="run_finished";D="terminal status";C=$C.Green},
        @{Y=417;T="error";D="sanitized failure";C=$C.Red}
    )
    foreach($e in $eventRows){
        Add-Box $s 438 $e.Y 482 38 $C.Fog $C.Line 5 | Out-Null
        Add-Pill $s 449 ($e.Y+7) 112 $e.T $e.C $C.White
        Add-Text $s 580 ($e.Y+10) 316 18 $e.D 11 $C.Slate | Out-Null
    }
    Add-Line $s 385 290 438 290 $C.Teal 2 $true | Out-Null
    Add-Text $s 397 267 30 17 "maps" 8.5 $C.Teal "Aptos" $true 2 | Out-Null
    Add-Footer $s

    # 13 - Defense in depth
    $s = $presentation.Slides.Add(13, 12); Set-Background $s $C.White
    Add-SlideTitle $s "Defense in depth contains failures at configuration, routing, tools, time, and publication" "Safety architecture" 13
    $controls=@(
        @{X=48;Y=122;W=270;T="1  INPUT / CONFIG";B="HTTPS-only provider and github.com repo`nTool calling required`nTask <= 20,000 characters`nToken metadata bounded";A=$C.Blue;F=$C.BlueSoft},
        @{X=345;Y=122;W=270;T="2  WORKFLOW";B="Explicit handoff edges`nReview + security cannot be skipped`nOnly release manager completes`nEmpty-diff developer guard";A=$C.Purple;F=$C.PurpleSoft},
        @{X=642;Y=122;W=270;T="3  TOOL BOUNDARY";B="No shell / execution / deployment`nPath + symlink checks`nFile allowlist + size limits`nBounded output";A=$C.Orange;F=$C.OrangeSoft},
        @{X=196;Y=316;W=270;T="4  RUNTIME";B="Fresh workflow per run`n20-minute session cancellation`n12 turns / agent`n40-message global ceiling";A=$C.Red;F=$C.RedSoft},
        @{X=493;Y=316;W=270;T="5  PUBLICATION";B="Exact diff before commit`nHuman approval required`nNew branch only`nPush and PR are explicit";A=$C.Teal;F=$C.TealSoft}
    )
    foreach($q in $controls){Add-CardText $s $q.X $q.Y $q.W 156 $q.T $q.B $q.A $q.F 14 11.5}
    Add-Footer $s

    # 14 - Deployment and code structure
    $s = $presentation.Slides.Add(14, 12); Set-Background $s $C.White
    Add-SlideTitle $s "Deployment is intentionally local and simple; the core stays reusable" "Physical architecture" 14
    Add-Box $s 40 124 530 335 $C.Fog $C.Line 10 | Out-Null
    Add-Text $s 62 144 490 20 "SOLUTION / PROCESS VIEW" 12 $C.Navy "Aptos" $true | Out-Null
    Add-Node $s 75 190 195 84 "Maf.Handoff.Api" "ASP.NET endpoints`nstatic browser UI`nsession singleton" $C.BlueSoft $C.Blue
    Add-Node $s 340 190 195 84 "Maf.Handoff.Cli" "advisory entry point`nenvironment settings" $C.Fog $C.Slate
    Add-Node $s 206 326 195 92 "Maf.Handoff.Core" "agents + workflow`nprovider adapters`nrepo / GitHub boundaries" $C.PurpleSoft $C.Purple
    Add-Line $s 172 274 271 326 $C.Blue 2 $true | Out-Null
    Add-Line $s 438 274 336 326 $C.Slate 2 $true | Out-Null
    Add-Text $s 80 439 450 15 "Single web process | in-memory sessions | temp-directory clones" 10.5 $C.Slate "Aptos" $true 2 | Out-Null
    Add-CardText $s 610 124 310 82 "Current operating model" "Local sample, one process, no user authentication, no durable session store." $C.Orange $C.OrangeSoft 14 10.5
    Add-CardText $s 610 219 310 82 "Scale boundary" "Session memory and local temporary clones bind a run to one process/host." $C.Red $C.RedSoft 14 10.5
    Add-CardText $s 610 314 310 82 "Production evolution" "Add identity, authorization, durable state/checkpoints, isolated workers and telemetry export." $C.Teal $C.TealSoft 14 10.5
    Add-Pill $s 640 422 249 "DEPLOYMENT TARGET: TRUSTED LOCAL HOST" $C.Navy $C.White
    Add-Footer $s

    # 15 - Summary
    $s = $presentation.Slides.Add(15, 12); Set-Background $s $C.Navy
    Add-Pill $s 48 42 130 "ARCHITECTURE" $C.Teal $C.White
    Add-Text $s 48 88 780 44 "The architecture is built around controlled authority" 30 $C.White "Aptos Display" $true | Out-Null
    Add-Text $s 48 141 770 30 "Reason broadly. Act narrowly. Publish deliberately." 17 $C.Mint "Aptos" $true | Out-Null
    $summary=@(
        @{X=48;N="01";T="Layers isolate concerns";B="UI, session lifecycle, workflow, models, repository tools, and publication are separate boundaries.";A=$C.Blue},
        @{X=345;N="02";T="Relationships enforce quality";B="A constrained handoff graph makes planning, correctness, security, and release evidence explicit.";A=$C.Purple},
        @{X=642;N="03";T="Humans retain authority";B="The system produces an exact patch; commit, push, and PR creation require explicit user choices.";A=$C.Teal}
    )
    foreach($q in $summary){
        Add-Box $s $q.X 218 270 196 $C.Navy2 $q.A 10 1.5 | Out-Null
        Add-Text $s ($q.X+20) 237 45 34 $q.N 22 $q.A "Aptos Display" $true | Out-Null
        Add-Text $s ($q.X+20) 287 230 42 $q.T 15 $C.White "Aptos" $true | Out-Null
        Add-Text $s ($q.X+20) 340 230 61 $q.B 11.5 $C.Line | Out-Null
    }
    Add-Line $s 48 462 912 462 $C.Slate 1 | Out-Null
    Add-Text $s 48 479 620 18 "Repository Change Workbench  |  C# reference implementation" 10.5 $C.Line | Out-Null
    Add-Text $s 738 477 174 20 "END" 11 $C.Mint "Aptos" $true 3 | Out-Null

    if (Test-Path -LiteralPath $pptxPath) { Remove-Item -LiteralPath $pptxPath -Force }
    if (Test-Path -LiteralPath $pdfPath) { Remove-Item -LiteralPath $pdfPath -Force }
    $presentation.SaveAs($pptxPath, 24)
    $presentation.SaveAs($pdfPath, 32)
}
finally {
    if ($presentation -ne $null) { $presentation.Close() }
    if ($powerPoint -ne $null) { $powerPoint.Quit() }
    if ($presentation -ne $null) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($presentation) }
    if ($powerPoint -ne $null) { [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($powerPoint) }
    [GC]::Collect(); [GC]::WaitForPendingFinalizers()
}

Write-Output "PPTX: $pptxPath"
Write-Output "PDF:  $pdfPath"
