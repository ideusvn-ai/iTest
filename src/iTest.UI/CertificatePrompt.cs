using System.Windows;
using ITest.Core;
using Microsoft.Win32;

namespace ITest.UI;

/// <summary>Hỏi người dùng file chứng chỉ sử dụng (.itcert), kiểm tra chữ ký và hạn dùng rồi lưu làm chứng chỉ của máy.</summary>
public static class CertificatePrompt
{
    /// <summary>
    /// Hiện lý do (chưa có chứng chỉ, hết hạn…) rồi cho chọn file. Lặp lại tới khi chọn được chứng chỉ hợp lệ
    /// hoặc người dùng bỏ qua (trả về null).
    /// </summary>
    public static Certificate? Ask(Window? owner, string reason)
    {
        while (true)
        {
            var text = reason + "\n\nBấm OK để chọn file chứng chỉ (.itcert). File này được gửi qua email khi trường đăng ký tài khoản trên trang web iTest; "
                     + "có thể lấy lại trong mục Tài khoản của trang web.";
            var answer = owner != null
                ? MessageBox.Show(owner, text, "Chứng chỉ sử dụng iTest", MessageBoxButton.OKCancel, MessageBoxImage.Information)
                : MessageBox.Show(text, "Chứng chỉ sử dụng iTest", MessageBoxButton.OKCancel, MessageBoxImage.Information);
            if (answer != MessageBoxResult.OK) return null;
            var dlg = new OpenFileDialog { Filter = CertificateStore.FileFilter, Title = "Chọn file chứng chỉ iTest" };
            if ((owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog()) != true) return null;
            try { return CertificateStore.Install(dlg.FileName); }
            catch (Exception ex)
            {
                reason = ex is InvalidDataException ? ex.Message : "Không đọc được file đã chọn: " + ex.Message;
            }
        }
    }
}
