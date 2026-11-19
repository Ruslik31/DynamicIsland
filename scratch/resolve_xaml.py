with open('MainWindow.xaml', 'r', encoding='utf-8') as f:
    text = f.read()

start_marker = '<<<<<<< HEAD\n'
mid_marker = '=======\n'
end_marker = '>>>>>>> origin/main\n'

start_idx = text.find(start_marker)
mid_idx = text.find(mid_marker, start_idx)
end_idx = text.find(end_marker, mid_idx) + len(end_marker)

assert start_idx != -1 and mid_idx != -1 and end_idx != -1

head_part = text[start_idx + len(start_marker):mid_idx]

old_menu_header = '''                <Grid x:Name="MenuView" Width="300" Height="408" HorizontalAlignment="Center" VerticalAlignment="Top">
                    <local:RowList Margin="10,14,10,0">
                        <TextBlock Style="{StaticResource Caption}" Text="DISCORD ISLAND" Margin="12,0,0,6"/>
                        <Button Style="{StaticResource RowButton}" Click="DiscordRow_Click">'''

new_menu_header = '''                <Grid x:Name="MenuView" Width="300" Height="420" HorizontalAlignment="Center" VerticalAlignment="Top">
                    <local:RowList Margin="10,14,10,0">
                        <TextBlock Style="{StaticResource Caption}" Text="DISCORD ISLAND" Margin="12,0,0,6"/>
                        <Button Style="{StaticResource MenuRowButton}" Click="DiscordRow_Click" local:RowList.Edge="Top">'''

assert old_menu_header in head_part, "old_menu_header not found in head_part"
head_part = head_part.replace(old_menu_header, new_menu_header)

timer_btn = '''                        <Button Style="{StaticResource RowButton}" Click="TimerRow_Click">\n'''
assert head_part.endswith(timer_btn), "timer_btn not at end of head_part"
head_part = head_part[:-len(timer_btn)]

# origin/main has the timer button inside it:
timer_origin = '                        <Button Style="{StaticResource MenuRowButton}" Click="TimerRow_Click" AutomationProperties.Name="Таймер">\n'
head_part = head_part + timer_origin

text = text[:start_idx] + head_part + text[end_idx:]

with open('MainWindow.xaml', 'w', encoding='utf-8') as f:
    f.write(text)
print('Resolved MainWindow.xaml successfully')
