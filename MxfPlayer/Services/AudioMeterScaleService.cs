using System;

namespace MxfPlayer.Services
{
    public class AudioMeterScaleService
    {
        public const double MinDb = -60.0;
        public const double MaxDb = 0.0;
        public const int MinBarHeight = 8;

        public double LevelToDb(float level)
        {
            if (level <= 0.000001f)
                return MinDb;

            double db = 20.0 * Math.Log10(level);
            return Math.Max(MinDb, Math.Min(MaxDb, db));
        }

        public double DbToRatio(double db)
        {
            db = Math.Max(MinDb, Math.Min(MaxDb, db));
            return (db - MinDb) / (MaxDb - MinDb);
        }

        public int DbToBarHeight(double db, int meterHeight)
        {
            double ratio = DbToRatio(db);
            return MinBarHeight + (int)Math.Round((meterHeight - MinBarHeight) * ratio);
        }

        public int DbToY(double db, int meterHeight)
        {
            double ratio = DbToRatio(db);

            // 0 dB 在上面，-60 dB 在下面
            return (int)Math.Round((1.0 - ratio) * meterHeight);
        }

        public int LevelToBarHeight(float level, int meterHeight)
        {
            double db = LevelToDb(level);
            return DbToBarHeight(db, meterHeight);
        }
    }
}