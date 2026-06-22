namespace BackupApp.Interfaces;

public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
    void Export(string filePath);
    void Import(string filePath);
}
