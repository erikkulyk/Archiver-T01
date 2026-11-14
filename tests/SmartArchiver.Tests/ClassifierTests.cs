using SmartArchiver;
using Xunit;

public class ClassifierTests
{
    [Fact]
    public void LoadSet_UsesTypeColumn_NotFileSizeColumn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smartarchiver-classifier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "text.txt"), System.Text.Encoding.UTF8.GetBytes("hello world"));
            File.WriteAllBytes(Path.Combine(dir, "image.pgm"), new byte[] { 0, 255, 0, 255 });
            File.WriteAllText(Path.Combine(dir, "labels.csv"),
                "file,type,type_name,size\n" +
                "text.txt,0,природний текст,11\n" +
                "image.pgm,3,растр без стиснення,4\n");

            var (x, y) = Classifier.LoadSet(dir);

            Assert.Equal(2, x.Count);
            Assert.Equal(new[] { 0, 3 }, y);
            Assert.All(x, features => Assert.Equal(Features.Count, features.Length));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadSet_RejectsMissingLabeledFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "smartarchiver-classifier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "labels.csv"), "file,type,type_name,size\nmissing.bin,4,виконуваний файл,12\n");
            Assert.Throws<InvalidDataException>(() => Classifier.LoadSet(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TrainBest_RejectsInvalidClassLabels()
    {
        var features = new List<double[]> { new double[Features.Count] };
        Assert.Throws<ArgumentException>(() => Classifier.TrainBest(
            features, new List<int> { 7 }, features, new List<int> { 0 }, seed: 1, inits: 1, hidden: 2, epochs: 1));
    }
}
