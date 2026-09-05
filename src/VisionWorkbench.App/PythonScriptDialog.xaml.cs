using System.Windows;

namespace VisionWorkbench.App;

public partial class PythonScriptDialog : Window
{
    private const string DefaultScript = """
# 固定输入变量：input_data
# 固定输出变量：result
# 所有任务统一从 input_data["items"] 读取对象

items = input_data.get("items", [])
kept = [
    i for i, item in enumerate(items)
    if item.get("confidence", 0) >= 0.8
]

result = {
    "keepIndices": kept,
    "status": "ok" if kept else "ng",
    "message": f"保留 {len(kept)} 个目标"
}
""";

    public string ScriptText => ScriptTextBox.Text;

    public PythonScriptDialog(string script)
    {
        InitializeComponent();
        ScriptTextBox.Text = string.IsNullOrWhiteSpace(script) ? DefaultScript : script;
        ScriptTextBox.Focus();
        ScriptTextBox.CaretIndex = ScriptTextBox.Text.Length;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ScriptTextBox.Text))
        {
            ThemedMessageBox.Show("脚本内容不能为空。", "配置校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
