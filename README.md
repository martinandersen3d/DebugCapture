# DebugCapture

# Preview TXT files with FZF

_list.bat:
```
powershell -NoProfile -c ls -Name *.txt | fzf --layout=reverse --preview-window=wrap --preview="type {}"
```