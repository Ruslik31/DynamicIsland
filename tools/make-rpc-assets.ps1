Add-Type -AssemblyName System.Drawing

function RoundRectPath([System.Drawing.Drawing2D.GraphicsPath]$path, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path.AddArc($x, $y, 2*$r, 2*$r, 180, 90)
    $path.AddArc($x + $w - 2*$r, $y, 2*$r, 2*$r, 270, 90)
    $path.AddArc($x + $w - 2*$r, $y + $h - 2*$r, 2*$r, 2*$r, 0, 90)
    $path.AddArc($x, $y + $h - 2*$r, 2*$r, 2*$r, 90, 90)
    $path.CloseFigure()
}

function New-Card([string]$outFile, [System.Drawing.Color]$c1, [System.Drawing.Color]$c2, [string]$kind) {
    $bmp = New-Object System.Drawing.Bitmap 512, 512
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    RoundRectPath $bg 0 0 512 512 110
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Rectangle]::new(0,0,512,512)), $c1, $c2, 45
    $g.FillPath($brush, $bg)

    # мягкий блик
    $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glow.AddEllipse(60, 40, 260, 260)
    $pth = New-Object System.Drawing.Drawing2D.PathGradientBrush ($glow)
    $pth.CenterColor = [System.Drawing.Color]::FromArgb(70, 255, 255, 255)
    $pth.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
    $g.FillPath($pth, $glow)

    $white = [System.Drawing.Brushes]::White
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 26
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    if ($kind -eq "music") {
        # нота: две головки, два ствола и флаг
        $g.FillEllipse($white, 130, 330, 92, 70)
        $g.FillEllipse($white, 290, 360, 92, 70)
        $g.FillRectangle($white, 212, 140, 20, 210)
        $g.FillRectangle($white, 372, 110, 20, 270)
        $flag = New-Object System.Drawing.Drawing2D.GraphicsPath
        $flag.AddBezier(232, 140, 300, 100, 340, 120, 392, 110)
        $flag.AddBezier(392, 110, 380, 180, 300, 170, 232, 190)
        $flag.CloseFigure()
        $g.FillPath($white, $flag)
    } else {
        # гарнитура: дуга и две чашки
        $g.DrawArc($pen, 120, 110, 272, 300, 180, 180)
        foreach ($x in 96, 340) {
            $cup = New-Object System.Drawing.Drawing2D.GraphicsPath
            RoundRectPath $cup $x 288 76 118 36
            $g.FillPath($white, $cup)
        }
        # волна звука внутри
        $wave = New-Object System.Drawing.Drawing2D.GraphicsPath
        $wave.AddBezier(206, 350, 226, 300, 246, 400, 266, 350)
        $wave.AddBezier(266, 350, 286, 300, 306, 400, 326, 350)
        $g.DrawPath($pen, $wave)
    }

    $bmp.Save($outFile, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

New-Card "assets\rpc\music.png" ([System.Drawing.Color]::FromArgb(255, 88, 101, 242)) ([System.Drawing.Color]::FromArgb(255, 235, 69, 158)) "music"
New-Card "assets\rpc\voice.png" ([System.Drawing.Color]::FromArgb(255, 35, 165, 90)) ([System.Drawing.Color]::FromArgb(255, 88, 101, 242)) "voice"
"saved"
