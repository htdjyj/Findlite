using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

public static class Theme {
    public static readonly Color Ink = Color.FromArgb(34, 44, 42), Muted = Color.FromArgb(101, 115, 106), Accent = Color.FromArgb(28, 98, 78), Line = Color.FromArgb(229, 234, 231);
    public static GraphicsPath Round(RectangleF r, float radius) {
        GraphicsPath p = new GraphicsPath(); float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right-d, r.Y, d, d, 270, 90); p.AddArc(r.Right-d, r.Bottom-d, d, d, 0, 90); p.AddArc(r.X, r.Bottom-d, d, d, 90, 90); p.CloseFigure(); return p;
    }
    public static void Mark(Graphics g, Rectangle bounds) {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath p = Round(bounds, bounds.Width*.24f)) using (Brush b = new SolidBrush(Accent)) g.FillPath(b, p);
        float s = bounds.Width / 40f, x = bounds.X, y = bounds.Y;
        using (Pen pen = new Pen(Color.White, 2.5f*s)) { g.DrawEllipse(pen,x+10*s,y+9*s,16*s,16*s); pen.StartCap=LineCap.Round; pen.EndCap=LineCap.Round; g.DrawLine(pen,x+24*s,y+24*s,x+31*s,y+31*s); }
        using (Pen p = new Pen(Color.FromArgb(170,211,190),1.8f*s)) { g.DrawLine(p,x+15*s,y+15*s,x+22*s,y+15*s); g.DrawLine(p,x+15*s,y+19*s,x+20*s,y+19*s); }
    }
}
public class QuietButton : Button {
    public bool Primary, Active, Navigation, Danger;
    bool hover, pressed;
    public QuietButton() {
        FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=0; Cursor=Cursors.Hand; Height=38;
        Font=new Font("Microsoft YaHei UI",9); BackColor=Color.White; Margin=new Padding(0,0,8,0);
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer, true);
    }
    public override Size GetPreferredSize(Size proposed) { return new Size(TextRenderer.MeasureText(Text,Font).Width+30,38); }
    protected override void OnMouseEnter(EventArgs e) { hover=true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover=pressed=false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed=true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed=false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e) {
        e.Graphics.Clear(Parent==null?Color.White:Parent.BackColor); e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
        Color fill = !Enabled ? Color.FromArgb(245,247,245) : Primary ? (pressed?Color.FromArgb(18,73,56):hover?Color.FromArgb(36,114,90):Theme.Accent) : Active ? Color.FromArgb(229,240,233) : hover ? Color.FromArgb(238,243,239) : (Navigation?Parent.BackColor:Color.White);
        Color text = !Enabled ? Color.FromArgb(167,174,170) : Primary ? Color.White : Active ? Theme.Accent : Danger ? Color.FromArgb(151,77,66) : Theme.Ink;
        RectangleF bounds=new RectangleF(.5f,.5f,Width-1,Height-1);
        using(GraphicsPath path=Theme.Round(bounds,8)) { using(Brush brush=new SolidBrush(fill)) e.Graphics.FillPath(brush,path); if(!Primary&&!Navigation) using(Pen pen=new Pen(Theme.Line)) e.Graphics.DrawPath(pen,path); }
        Rectangle area=ClientRectangle; if(Navigation) { area.X+=18; area.Width-=28; }
        TextRenderer.DrawText(e.Graphics,Text,Font,area,text,TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|(Navigation?TextFormatFlags.Left:TextFormatFlags.HorizontalCenter));
        if(Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics,Rectangle.Inflate(ClientRectangle,-4,-4),text,fill);
    }
}
public class BrandMark : Control {
    public BrandMark() { DoubleBuffered=true; }
    protected override void OnPaint(PaintEventArgs e) { Theme.Mark(e.Graphics,new Rectangle(1,1,Width-2,Height-2)); }
}
public class SearchMark : Control {
    public SearchMark() { DoubleBuffered=true; }
    protected override void OnPaint(PaintEventArgs e) { e.Graphics.SmoothingMode=SmoothingMode.AntiAlias; using(Pen p=new Pen(Theme.Muted,1.7f)) { e.Graphics.DrawEllipse(p,4,5,13,13); e.Graphics.DrawLine(p,15,17,21,23); } }
}
public class ResultList : ListView {
    public ResultList() { DoubleBuffered=true; OwnerDraw=true; }
    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e) {
        using(Brush b=new SolidBrush(Color.FromArgb(249,250,249))) e.Graphics.FillRectangle(b,e.Bounds);
        Rectangle r=e.Bounds; r.X+=12; TextRenderer.DrawText(e.Graphics,e.Header.Text,Font,r,Theme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine);
        using(Pen p=new Pen(Theme.Line)) e.Graphics.DrawLine(p,e.Bounds.Left,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1);
    }
    protected override void OnDrawItem(DrawListViewItemEventArgs e) { if(View!=View.Details) e.DrawDefault=true; }
    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e) {
        bool selected=e.Item.Selected;
        using(Brush b=new SolidBrush(selected?Color.FromArgb(232,242,235):Color.White)) e.Graphics.FillRectangle(b,e.Bounds);
        using(Pen p=new Pen(Color.FromArgb(245,247,245))) e.Graphics.DrawLine(p,e.Bounds.Left,e.Bounds.Bottom-1,e.Bounds.Right,e.Bounds.Bottom-1);
        Rectangle r=e.Bounds; r.X+=12; r.Width-=22;
        if(e.ColumnIndex==0) {
            FileEntry item=e.Item.Tag as FileEntry; Rectangle icon=new Rectangle(r.Left,r.Top+(r.Height-16)/2,14,16);
            using(Pen p=new Pen(item!=null&&item.Folder?Color.FromArgb(135,153,126):Color.FromArgb(148,167,165),1.2f)) {
                if(item!=null&&item.Folder) { e.Graphics.DrawRectangle(p,icon.X,icon.Y+4,14,10); e.Graphics.DrawLine(p,icon.X,icon.Y+4,icon.X+5,icon.Y+1); e.Graphics.DrawLine(p,icon.X+5,icon.Y+1,icon.X+9,icon.Y+4); }
                else { e.Graphics.DrawRectangle(p,icon.X+1,icon.Y+1,11,14); e.Graphics.DrawLine(p,icon.X+4,icon.Y+6,icon.X+9,icon.Y+6); e.Graphics.DrawLine(p,icon.X+4,icon.Y+9,icon.X+9,icon.Y+9); }
            }
            r.X+=28; r.Width-=28;
        }
        TextRenderer.DrawText(e.Graphics,e.SubItem.Text,Font,r,e.ColumnIndex==0?Theme.Ink:Theme.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
    }
}
